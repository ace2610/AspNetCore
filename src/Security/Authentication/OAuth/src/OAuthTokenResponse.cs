// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Text.Json;

namespace Microsoft.AspNetCore.Authentication.OAuth
{
    public class OAuthTokenResponse
    {
        private OAuthTokenResponse(JsonDocument response)
        {
            Response = response;

            AccessToken = GetString(response, "access_token");
            AccessToken = GetString(response, "access_token");
            TokenType = GetString(response, "token_type");
            RefreshToken = GetString(response, "refresh_token");
            ExpiresIn = GetString(response, "expires_in");
        }

        private static string GetString(JsonDocument document, string key) =>
            document.RootElement.TryGetProperty(key, out var property)
                ? property.ToString() : null;

        private OAuthTokenResponse(Exception error)
        {
            Error = error;
        }

        public static OAuthTokenResponse Success(JsonDocument response)
        {
            return new OAuthTokenResponse(response);
        }

        public static OAuthTokenResponse Failed(Exception error)
        {
            return new OAuthTokenResponse(error);
        }

        public JsonDocument Response { get; set; }
        public string AccessToken { get; set; }
        public string TokenType { get; set; }
        public string RefreshToken { get; set; }
        public string ExpiresIn { get; set; }
        public Exception Error { get; set; }
    }
}
