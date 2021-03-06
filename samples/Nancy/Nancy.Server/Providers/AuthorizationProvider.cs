﻿using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using AspNet.Security.OpenIdConnect.Primitives;
using Nancy.Server.Models;
using Owin.Security.OpenIdConnect.Server;

namespace Nancy.Server.Providers
{
    public class AuthorizationProvider : OpenIdConnectServerProvider
    {
        public override Task MatchEndpoint(MatchEndpointContext context)
        {
            // Note: by default, OpenIdConnectServerHandler only handles authorization requests made to the authorization endpoint.
            // This notification handler uses a more relaxed policy that allows extracting authorization requests received at
            // /connect/authorize/accept and /connect/authorize/deny (see AuthorizationModule.cs for more information).
            if (context.Options.AuthorizationEndpointPath.HasValue &&
                context.Request.Path.StartsWithSegments(context.Options.AuthorizationEndpointPath))
            {
                context.MatchAuthorizationEndpoint();
            }

            return Task.FromResult(0);
        }

        public override async Task ValidateAuthorizationRequest(ValidateAuthorizationRequestContext context)
        {
            // Note: the OpenID Connect server middleware supports the authorization code, implicit and hybrid flows
            // but this authorization provider only accepts authorization code or hybrid flow authorization requests
            // that don't result in an access token being returned directly from the authorization endpoint.
            // You may consider relaxing it to support the implicit flow. In this case, consider adding checks
            // rejecting implicit/hybrid authorization requests when the client is a confidential application.
            if (!context.Request.IsAuthorizationCodeFlow() && !context.Request.IsHybridFlow() ||
                 context.Request.HasResponseType(OpenIdConnectConstants.ResponseTypes.Token))
            {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    description: "Only response_type=code and response_type=id_token or " +
                                 "response_type=code id_token are supported by this authorization server.");

                return;
            }

            // Note: to support custom response modes, the OpenID Connect server middleware doesn't
            // reject unknown modes before the ApplyAuthorizationResponse event is invoked.
            // To ensure invalid modes are rejected early enough, a check is made here.
            if (!string.IsNullOrEmpty(context.Request.ResponseMode) && !context.Request.IsFormPostResponseMode() &&
                                                                       !context.Request.IsFragmentResponseMode() &&
                                                                       !context.Request.IsQueryResponseMode())
            {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidRequest,
                    description: "The specified 'response_mode' is unsupported.");

                return;
            }

            using (var database = new ApplicationContext())
            {
                // Retrieve the application details corresponding to the requested client_id.
                var application = await (from entity in database.Applications
                                         where entity.ApplicationID == context.ClientId
                                         select entity).SingleOrDefaultAsync(context.OwinContext.Request.CallCancelled);

                if (application == null)
                {
                    context.Reject(
                        error: OpenIdConnectConstants.Errors.InvalidClient,
                        description: "The specified client identifier is invalid.");

                    return;
                }

                if (!string.IsNullOrEmpty(context.RedirectUri) &&
                    !string.Equals(context.RedirectUri, application.RedirectUri, StringComparison.Ordinal))
                {
                    context.Reject(
                        error: OpenIdConnectConstants.Errors.InvalidClient,
                        description: "The specified 'redirect_uri' is invalid.");

                    return;
                }

                context.Validate(application.RedirectUri);
            }
        }

        public override async Task ValidateTokenRequest(ValidateTokenRequestContext context)
        {
            // Note: the OpenID Connect server middleware supports authorization code, refresh token, client credentials
            // and resource owner password credentials grant types but this authorization provider uses a safer policy
            // rejecting the last two ones. You may consider relaxing it to support the ROPC or client credentials grant types.
            if (!context.Request.IsAuthorizationCodeGrantType() && !context.Request.IsRefreshTokenGrantType())
            {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.UnsupportedGrantType,
                    description: "Only authorization code and refresh token grant types " +
                                 "are accepted by this authorization server.");

                return;
            }

            // Note: client authentication is not mandatory for non-confidential client applications like mobile apps
            // (except when using the client credentials grant type) but this authorization server uses a safer policy
            // that makes client authentication mandatory and returns an error if client_id or client_secret is missing.
            // You may consider relaxing it to support the resource owner password credentials grant type
            // with JavaScript or desktop applications, where client credentials cannot be safely stored.
            // In this case, call context.Skip() to inform the server middleware the client is not trusted.
            if (string.IsNullOrEmpty(context.ClientId) || string.IsNullOrEmpty(context.ClientSecret))
            {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidRequest,
                    description: "The mandatory 'client_id'/'client_secret' parameters are missing.");

                return;
            }

            using (var database = new ApplicationContext())
            {
                // Retrieve the application details corresponding to the requested client_id.
                var application = await (from entity in database.Applications
                                         where entity.ApplicationID == context.ClientId
                                         select entity).SingleOrDefaultAsync(context.OwinContext.Request.CallCancelled);

                if (application == null)
                {
                    context.Reject(
                        error: OpenIdConnectConstants.Errors.InvalidClient,
                        description: "The specified client identifier is invalid.");

                    return;
                }

                // Note: to mitigate brute force attacks, you SHOULD strongly consider applying
                // a key derivation function like PBKDF2 to slow down the secret validation process.
                // You SHOULD also consider using a time-constant comparer to prevent timing attacks.
                // For that, you can use the CryptoHelper library developed by @henkmollema:
                // https://github.com/henkmollema/CryptoHelper. If you don't need .NET Core support,
                // SecurityDriven.NET/inferno is a rock-solid alternative: http://securitydriven.net/inferno/
                if (!string.Equals(context.ClientSecret, application.Secret, StringComparison.Ordinal))
                {
                    context.Reject(
                        error: OpenIdConnectConstants.Errors.InvalidClient,
                        description: "The specified client credentials are invalid.");

                    return;
                }

                context.Validate();
            }
        }

        public override async Task ValidateLogoutRequest(ValidateLogoutRequestContext context)
        {
            using (var database = new ApplicationContext())
            {
                // When provided, post_logout_redirect_uri must exactly
                // match the address registered by the client application.
                if (!string.IsNullOrEmpty(context.PostLogoutRedirectUri) &&
                    !await database.Applications.AnyAsync(application => application.LogoutRedirectUri == context.PostLogoutRedirectUri))
                {
                    context.Reject(
                        error: OpenIdConnectConstants.Errors.InvalidRequest,
                        description: "The specified 'post_logout_redirect_uri' is invalid.");

                    return;
                }

                context.Validate();
            }
        }
    }
}
