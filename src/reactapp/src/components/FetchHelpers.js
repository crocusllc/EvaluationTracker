// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

import { transform as _transform } from "lodash-es";
import { getToken, validateAuthenticationToken } from "../components/TokenHelpers";


// TODO: replace hard-coded with some sort of runtime setting
// Will be fixed in EPPETA-19.
//const BaseUrl = "https://localhost:7065";
const BaseUrl = "https://localhost:44390"

const modify = async (verb, route, values) => {
  if (!route.startsWith("/")) {
    route = `/${route}`;
  }

  const request = {
    method: verb,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(values),
  };
  await validateAuthenticationToken();
  const token = getToken();
  if (token) {
    request.headers["Authorization"] = `bearer ${token}`;
  }

  const url = `${BaseUrl}${route}`;

  return await fetch(url, request);

  // TODO: might be good to detect token failure here, maybe redirecting to
  // signin. Or at least insuring there is a user-friendly error message.
  // EPPETA-21.
};

const get = async (route) => {
  return await modify("GET", route);
};

const post = async (route, values) => {
  return await modify("POST", route, values);
};

// eslint-disable-next-line no-unused-vars
const put = async (route, values) => {
  return await modify("PUT", route, values);
};

const postForm = async (route, values) => {
  // Transform the `values` dictionary to a form-encoded string
  const form = _transform(
    values,
    (result, value, key) => {
      result.push(`${encodeURIComponent(key)}=${encodeURIComponent(value)}`);
    },
    []
  ).join("&");

  const url = `${BaseUrl}${route}`;
  const request = {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded;charset=UTF-8" },
    body: form,
  };

  return await fetch(url, request);
};

export { post, postForm, get, put };
