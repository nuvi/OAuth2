using System.Collections.Specialized;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OAuth2.Configuration;
using OAuth2.Infrastructure;
using OAuth2.Models;
using RestSharp;
using RestSharp.Contrib;
using System.Net;

namespace OAuth2.Client
{
    /// <summary>
    /// Base class for OAuth2 client implementation.
    /// </summary>
    public abstract class OAuth2Client : IClient
    {
        private const string AccessTokenKey = "access_token";

        private readonly IRequestFactory _factory;

        /// <summary>
        /// Client configuration object.
        /// </summary>
        public IClientConfiguration Configuration { get; private set; }

        /// <summary>
        /// Friendly name of provider (OAuth2 service).
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// State (any additional information that was provided by application and is posted back by service).
        /// </summary>
        public string State { get; private set; }

        /// <summary>
        /// Access token returned by provider. Can be used for further calls of provider API.
        /// </summary>
        public string AccessToken { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="OAuth2Client"/> class.
        /// </summary>
        /// <param name="factory">The factory.</param>
        /// <param name="configuration">The configuration.</param>
        protected OAuth2Client(IRequestFactory factory, IClientConfiguration configuration)
        {
            _factory = factory;
            Configuration = configuration;
        }

        /// <summary>
        /// Returns URI of service which should be called in order to start authentication process.
        /// This URI should be used for rendering login link.
        /// </summary>
        /// <param name="state">
        /// Any additional information that will be posted back by service.
        /// </param>
        public virtual string GetLoginLinkUri(string state = null)
        {
            var client = _factory.CreateClient(AccessCodeServiceEndpoint);
            var request = _factory.CreateRequest(AccessCodeServiceEndpoint);
            request.AddObject(new
            {
                response_type = "code",
                client_id = Configuration.ClientId,
                redirect_uri = Configuration.RedirectUri,
                scope = Configuration.Scope,
                state
            });
            return client.BuildUri(request).ToString();
        }

        /// <summary>
        /// When the domain is not nuvi.com, such as whitelabeled domains.
        /// Returns URI of service which should be called in order to start authentication process.
        /// This URI includes the authentication endpoint and a redirect url,
        /// and should be used for rendering login link.
        /// </summary>
        /// <param name="requestScheme">Specifies http or https></param>
        /// <param name="redirectDomain">
        /// The domain for the redirect url after authentication.
        /// </param>
        public virtual string GetCustomDomainLoginLinkUri(string requestScheme, string redirectDomain, string state = null)
        {
            var scheme = requestScheme + "://";
            var baseUri = scheme + redirectDomain;

            var authEndpoint = CustomDomainAccessCodeServiceEndpoint(baseUri);
            var redirectUri = System.Uri.EscapeUriString(scheme + redirectDomain + Configuration.AuthPath);

            var client = _factory.CreateClient(authEndpoint);
            var request = _factory.CreateRequest(authEndpoint);

            request.AddObject(new
                {
                    response_type = "code",
                    client_id = Configuration.ClientId,
                    redirect_uri = redirectUri,
                    scope = Configuration.Scope,
                    state
                });
            return client.BuildUri(request).ToString();
        }

        /// <summary>
        /// Obtains user information using OAuth2 service and data provided via callback request.
        /// </summary>
        /// <param name="parameters">Callback request payload (parameters).</param>
        public UserInfo GetUserInfo(NameValueCollection parameters)
        {
            CheckErrorAndSetState(parameters);
            QueryAccessToken(parameters);
            return GetUserInfo();
        }

        /// <summary>
        /// Obtains user information using OAuth2 service and data provided via callback request.
        /// Use case is for customers with custom domains (i.e. Whitelabel)
        /// </summary>
        /// <returns>The user info for custom domain.</returns>
        /// <param name="parameters">Query Parameters.</param>
        /// <param name="isSecure">Specifies whether or not the request is https or not.</param>
        /// <param name="customDomain">Custom domain for whitelabel company.</param>
        public UserInfo GetCustomDomainUserInfo(NameValueCollection parameters, string requestScheme, string customDomain)
        {
            CheckErrorAndSetState(parameters);
            CustomDomainQueryAccessToken(parameters, requestScheme, customDomain);
            return GetUserInfo();
        }

        /// <summary>
        /// Issues query for access token and returns access token.
        /// </summary>
        /// <param name="parameters">Callback request payload (parameters).</param>
        public string GetToken(NameValueCollection parameters)
        {
            CheckErrorAndSetState(parameters);
            QueryAccessToken(parameters);
            return AccessToken;
        }

        /// <summary>
        /// Defines URI of service which issues access code.
        /// </summary>
        protected abstract Endpoint AccessCodeServiceEndpoint { get; }

        private Endpoint CustomDomainAccessCodeServiceEndpoint(string baseUri)
        {
            Endpoint authEndpoint = new Endpoint();
            authEndpoint.BaseUri = baseUri;
            authEndpoint.Resource = "/authenticate/oauth/authorize";
            return authEndpoint;
        }

        /// <summary>
        /// Defines URI of service which issues access token.
        /// </summary>
        protected abstract Endpoint AccessTokenServiceEndpoint { get; }

        private Endpoint CustomDomainAccessTokenServiceEndpoint(string baseUri)
        {
            Endpoint tokenEndpoint = new Endpoint();
            tokenEndpoint.BaseUri = baseUri;
            tokenEndpoint.Resource = "/authenticate/oauth/token";
            return tokenEndpoint;
        }

        /// <summary>
        /// Defines URI of service which allows to obtain information about user
        /// who is currently logged in.
        /// </summary>
        protected abstract Endpoint UserInfoServiceEndpoint { get; }

        private void CheckErrorAndSetState(NameValueCollection parameters)
        {
            const string errorFieldName = "error";
            var error = parameters[errorFieldName];
            if (!error.IsEmpty())
            {
                throw new UnexpectedResponseException(errorFieldName);
            }

            State = parameters["state"];
        }

        /// <summary>
        /// Issues query for access token and parses response.
        /// </summary>
        /// <param name="parameters">Callback request payload (parameters).</param>
        private void QueryAccessToken(NameValueCollection parameters)
        {
            var client = _factory.CreateClient(AccessTokenServiceEndpoint);
            var request = _factory.CreateRequest(AccessTokenServiceEndpoint, Method.POST);

			BeforeGetAccessToken(new BeforeAfterRequestArgs
            {
                Client = client,
                Request = request,
                Parameters = parameters,
                Configuration = Configuration
            });



            var response = client.ExecuteAndVerify(request);

            AfterGetAccessToken(new BeforeAfterRequestArgs
            {
                Response = response,
                Parameters = parameters
            });

            AccessToken = ParseAccessTokenResponse(response.Content);
        }

        /// <summary>
        /// Issues a query access token from the custom domain.
        /// Use case is for whitelabel company authentication.
        /// </summary>
        /// <param name="parameters">Query parameters.</param>
        /// <param name="requestScheme">Specifies https or http.</param>
        /// <param name="customDomain">Custom domain for whitelabel company.</param>
        private void CustomDomainQueryAccessToken(NameValueCollection parameters, string requestScheme, string customDomain)
        {
            var scheme = requestScheme + "://";
            var baseUri = scheme + customDomain;

            var tokenServiceEndpoint = CustomDomainAccessTokenServiceEndpoint(baseUri);
            var client = _factory.CreateClient(tokenServiceEndpoint);
            var request = _factory.CreateRequest(tokenServiceEndpoint, Method.POST);

            var redirectUri = System.Uri.EscapeUriString(scheme + customDomain + Configuration.AuthPath);

            BeforeGetAccessToken(new BeforeAfterRequestArgs
                {
                    Client = client,
                    Request = request,
                    Parameters = parameters,
                    Configuration = Configuration,
                    RedirectUri = redirectUri
                });

            var response = client.ExecuteAndVerify(request);

            AfterGetAccessToken(new BeforeAfterRequestArgs
                {
                    Response = response,
                    Parameters = parameters
                });

            AccessToken = ParseAccessTokenResponse(response.Content);
        }

        protected virtual string ParseAccessTokenResponse(string content)
        {
            try
            {
                // response can be sent in JSON format
                var token = (string)JObject.Parse(content).SelectToken(AccessTokenKey);
                if (token.IsEmpty())
                {
                    throw new UnexpectedResponseException(AccessTokenKey);
                }
                return token;
            }
            catch (JsonReaderException)
            {
                // or it can be in "query string" format (param1=val1&param2=val2)
                var collection = HttpUtility.ParseQueryString(content);
                return collection.GetOrThrowUnexpectedResponse(AccessTokenKey);
            }
        }

        /// <summary>
        /// Should return parsed <see cref="UserInfo"/> using content received from provider.
        /// </summary>
        /// <param name="content">The content which is received from provider.</param>
        protected abstract UserInfo ParseUserInfo(string content);

        protected virtual void BeforeGetAccessToken(BeforeAfterRequestArgs args)
        {
            args.Request.AddObject(new
            {
                code = args.Parameters.GetOrThrowUnexpectedResponse("code"),
                client_id = Configuration.ClientId,
                client_secret = Configuration.ClientSecret,
                redirect_uri = args.RedirectUri ?? Configuration.RedirectUri,
                grant_type = "authorization_code"
            });
        }

        /// <summary>
        /// Called just after obtaining response with access token from service.
        /// Allows to read extra data returned along with access token.
        /// </summary>
        protected virtual void AfterGetAccessToken(BeforeAfterRequestArgs args)
        {
        }

        /// <summary>
        /// Called just before issuing request to service when everything is ready.
        /// Allows to add extra parameters to request or do any other needed preparations.
        /// </summary>
        protected virtual void BeforeGetUserInfo(BeforeAfterRequestArgs args)
        {
        }

        /// <summary>
        /// Obtains user information using provider API.
        /// </summary>
        protected virtual UserInfo GetUserInfo()
        {
            var client = _factory.CreateClient(UserInfoServiceEndpoint);
            client.Authenticator = new OAuth2UriQueryParameterAuthenticator(AccessToken);
            var request = _factory.CreateRequest(UserInfoServiceEndpoint);

            BeforeGetUserInfo(new BeforeAfterRequestArgs
            {
                Client = client,
                Request = request,
                Configuration = Configuration
            });

            var response = client.ExecuteAndVerify(request);

            var result = ParseUserInfo(response.Content);
            result.ProviderName = Name;

            return result;
        }
    }
}