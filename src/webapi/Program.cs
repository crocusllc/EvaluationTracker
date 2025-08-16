// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi.Models;
using Quartz;
using Serilog;

using eppeta.webapi.Account.Data;
using eppeta.webapi.Evaluations.Data;
using eppeta.webapi.Identity.Data;
using eppeta.webapi.Identity.Models;
using eppeta.webapi.Infrastructure;
using eppeta.webapi.Service;
using eppeta.webapi.Swagger;

namespace eppeta.webapi;

internal class Program
{
    private const string ConnectionStringName = "DefaultConnection";
    private const string TokenEndpoint = "connect/token";
    private const string AllowedOriginsPolicy = "AllowedOriginsPolicy";

    private static async Task Main(string[] args)
    {
        // For logging before we've read the log settings
        Log.Logger = new LoggerConfiguration()
               .Enrich.FromLogContext()
               .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {Level:u3} {Message:lj}{NewLine}{Exception}")
               .CreateBootstrapLogger();

        try
        {
            Log.Information("Starting EPP Evaluation Tracker API...");
            var builder = WebApplication.CreateBuilder(args);
            AppSettings.Initialize(builder.Configuration);
            // Use the TrustAllSSLCerts method in the AppSettings class to trust all SSL certificates.
            AppSettings.OptionallyTrustAllSSLCerts();
            // get token lifetime: minutes
            int authenticationTokenTimeout = int.Parse(builder.Configuration["AuthenticationTokenTimeout"] ?? "15");
            // refresh token lifetime: days
            int authenticationRefreshTokenLifeTime = int.Parse(builder.Configuration["AuthenticationRefreshTokenLifeTime"] ?? "15");

            // Add services to the container.
            _ = builder.Services.AddControllers();
            ConfigureLogging(builder);
            ConfigureWebHost(builder);
            ConfigureCorsService(builder.Services);
            ConfigureDatabaseConnection(builder);
            ConfigureLocalIdentityProvider(builder.Services, authenticationTokenTimeout, authenticationRefreshTokenLifeTime);
            ConfigureQuartz(builder.Services);
            ConfigureSwaggerUIServices(builder.Services);
            ConfigureAspNetAuth(builder.Services);

            // Add authentication configuration service to the container.
            _ = builder.Services.AddScoped<IODSAPIAuthenticationConfigurationService>(
                provider => new ODSAPIAuthenticationConfigurationService(builder.Configuration["OdsApiBasePath"] ?? string.Empty, builder?.Configuration["ODSAPIKey"] ?? string.Empty, builder?.Configuration["ODSAPISecret"] ?? string.Empty)
            );

            // Sync ODS Assets
            _ = builder.Services.AddScoped<SyncOdsAssets>();
            _ = builder.Services.AddSingleton<PeriodicHostedSyncOdsAssetsService>();
            _ = builder.Services.AddHostedService(provider => provider.GetRequiredService<PeriodicHostedSyncOdsAssetsService>());

            _ = builder.Services.AddSingleton<ODSToOITBackgroundService>();
            _ = builder.Services.AddHostedService<ODSToOITBackgroundService>(provider => provider.GetRequiredService<ODSToOITBackgroundService>());

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            ConfigureSwaggerUIApp(app);
            _ = app.UseForwardedHeaders();
            _ = app.UseMiddleware<LoggingMiddleware>();
            _ = app.UseRouting();
            _ = app.UseCors(AllowedOriginsPolicy);
            _ = app.UseAuthentication();
            _ = app.UseAuthorization();
            _ = app.UseEndpoints(endpoints =>
            {
                _ = endpoints.MapControllers();
                _ = endpoints.MapDefaultControllerRoute();
            });

            //A get route shall return the current state of our background sync service:
            _ = app.MapGet("/syncOdsAssets", (
                PeriodicHostedSyncOdsAssetsService service) =>
                {
                    return new PeriodicHostedSyncOdsAssetsServiceState(service.IsEnabled);
                });

            //And a patch route shall let us set the desired state of our background sync service:
            _ = app.MapMethods("/syncOdsAssets", new[] { "PATCH" }, (
                PeriodicHostedSyncOdsAssetsServiceState state,
                PeriodicHostedSyncOdsAssetsService service) =>
                {
                    service.IsEnabled = state.IsEnabled;
                });

            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application start-up failed");
            Environment.Exit(1);
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }

        static void ConfigureLogging(WebApplicationBuilder builder)
        {
            _ = builder.Host.UseSerilog((context, services, configuration) =>
                configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
            );
        }

        static void ConfigureWebHost(WebApplicationBuilder builder)
        {
            _ = builder.WebHost.ConfigureKestrel(
                // Security through obscurity: don't add a header revealing the web server
                serverOptions => { serverOptions.AddServerHeader = false; });
        }

        static void ConfigureCorsService(IServiceCollection services)
        {
            _ = services.AddCors(options =>
            {
                options.AddPolicy(name: AllowedOriginsPolicy, policy =>
                {
                    _ = policy.WithOrigins(AppSettings.AllowedOrigins)
                          .WithHeaders(HeaderNames.ContentType, "Content-Type")
                          .WithHeaders(HeaderNames.Authorization, "Authorization")
                          .WithMethods("PUT", "GET", "POST"); ;
                });
            });
        }

        // When registering multiple DbContext types, make sure that the constructor
        // for each context type has a DbContextOptions<TContext> parameter rather
        // than a non-generic DbContextOptions parameter
        static void ConfigureDatabaseConnection(WebApplicationBuilder builder)
        {
            var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName) ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' not found.");

            _ = builder.Services.AddDbContext<IdentityDbContext>(options =>
            {
                _ = options.UseSqlServer(connectionString);
                _ = options.UseOpenIddict();
            });

            _ = builder.Services.AddDbContext<EvaluationDbContext>(options =>
            {
                _ = options.UseSqlServer(connectionString);
            });
            _ = builder.Services.AddDbContext<AccountDbContext>(options =>
            {
                _ = options.UseSqlServer(connectionString);
                _ = options.UseOpenIddict();
            });

            _ = builder.Services.AddScoped<IIdentityRepository, IdentityDbContext>();
            _ = builder.Services.AddScoped<IEvaluationRepository, EvaluationDbContext>();
            _ = builder.Services.AddScoped<IAccountRepository, AccountDbContext>();
        }



        static void ConfigureLocalIdentityProvider(IServiceCollection services, int authenticationTokenTimeout, int authenticationRefreshTokenLifeTime)
        {
            TimeSpan accessTokenLifetime = TimeSpan.FromMinutes(authenticationTokenTimeout);
            TimeSpan refreshTokenLifetime = TimeSpan.FromDays(authenticationRefreshTokenLifeTime);
            _ = services.AddIdentity<ApplicationUser, IdentityRole>()
                .AddEntityFrameworkStores<IdentityDbContext>()
                .AddDefaultTokenProviders();

            _ = services.AddOpenIddict()
                .AddCore(options =>
                {
                    _ = options.UseEntityFrameworkCore()
                        .UseDbContext<IdentityDbContext>();
                    _ = options.UseQuartz();
                })
                .AddServer(options =>
                {
                    _ = options.SetTokenEndpointUris(TokenEndpoint);
                    // Add Token timeout
                    _ = options.SetAccessTokenLifetime(accessTokenLifetime);
                    // These two go hand-in-hand: allowing anonymous client means you can
                    // send password request without _also_ providing a client_id.
                    options.AllowPasswordFlow();
                    options.AllowRefreshTokenFlow();
                    options.AcceptAnonymousClients();
                    options.SetRefreshTokenLifetime(refreshTokenLifetime);
                    // Turned off token encryption, will discusss need for encrypted JWT later in project development
                    _ = options.DisableAccessTokenEncryption();

                    _ = options.AddDevelopmentEncryptionCertificate()
                        .AddDevelopmentSigningCertificate();
                    var aspNetCoreBuilder = options.UseAspNetCore()
                        .EnableTokenEndpointPassthrough();

                    if (!AppSettings.Authentication.RequireHttps)
                    {
                        _ = aspNetCoreBuilder.DisableTransportSecurityRequirement();
                    }

                    _ = options.AddSigningKey(AppSettings.Authentication.SigningKey ?? throw new InvalidOperationException("Unable to initialize Identity because there is no Signing Key configured in appsettings"));
                    _ = options.SetIssuer(AppSettings.Authentication.IssuerUrl ?? throw new InvalidOperationException("Unable to initialize Identity because there is no Issuer URL configured in appsettings"));
                })
                .AddValidation(options =>
                {
                    _ = options.UseLocalServer();
                    _ = options.UseAspNetCore();
                    _ = options.Configure(options => options.TokenValidationParameters.IssuerSigningKey = AppSettings.Authentication.SigningKey);
                });
        }

        static void ConfigureAspNetAuth(IServiceCollection services)
        {
            _ = services.AddAuthentication(opt =>
            {
                opt.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                opt.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            }).AddJwtBearer(opt =>
            {
                opt.Authority = AppSettings.Authentication.Authority;
                opt.SaveToken = true;
                opt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidAudience = AppSettings.Authentication.Audience,
                    ValidIssuer = AppSettings.Authentication.IssuerUrl!.ToString(),
                    IssuerSigningKey = AppSettings.Authentication.SigningKey
                };
                opt.RequireHttpsMetadata = AppSettings.Authentication.RequireHttps;
            });
            _ = services.AddAuthorization();
        }

        static void ConfigureQuartz(IServiceCollection services)
        {
            // Quartz will be used for scheduled removal of old authorization tokens
            _ = services.AddQuartz(options =>
            {
                options.UseMicrosoftDependencyInjectionJobFactory();
                options.UseSimpleTypeLoader();
                options.UseInMemoryStore();
            });
            _ = services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
        }

        static void ConfigureSwaggerUIServices(IServiceCollection services)
        {
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            _ = services.AddEndpointsApiExplorer();
            _ = services.AddSwaggerGen(options =>
            {
                options.CustomSchemaIds(x => x.FullName?.Replace("+", "."));
                options.OperationFilter<TokenEndpointBodyDescriptionFilter>();
                options.OperationFilter<TagByResourceUrlFilter>();
                options.AddSecurityDefinition("bearerAuth", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "JWT Authorization header using the Bearer scheme."
                });
                options.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "bearerAuth" }
                        },
                        Array.Empty<string>()
                    }
                });

                options.DocumentFilter<ExplicitSchemaDocumentFilter>();
                options.DocumentFilter<SwaggerIgnoreDocumentFilter>();
                options.SchemaFilter<SwaggerOptionalSchemaFilter>();
                options.SchemaFilter<SwaggerExcludeSchemaFilter>();
                options.EnableAnnotations();
                options.OrderActionsBy(x =>
                {
                    return x.HttpMethod != null && Enum.TryParse<HttpVerbOrder>(x.HttpMethod, out var verb)
                        ? ((int)verb).ToString()
                        : int.MaxValue.ToString();
                });
                options.SwaggerDoc("v1", new OpenApiInfo { Title = "EPP Evaluation Tracker", Version = "v1" });
            });
        }

        static void ConfigureSwaggerUIApp(WebApplication app)
        {
            if (app.Configuration.GetValue<bool>("EnableSwagger"))
            {
                _ = app.UseSwagger();
                _ = app.UseSwaggerUI(options =>
                {
                    options.EnableTryItOutByDefault();
                    options.DocumentTitle = "EPP Evaluation Tracker API Documentation";
                });
            }
        }
    }
}
