using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text;
using Newtonsoft.Json;
using FlagsmithEngine.Environment.Models;
using FlagsmithEngine;
using FlagsmithEngine.Interfaces;
using FlagsmithEngine.Identity.Models;
using FlagsmithEngine.Segment;
using FlagsmithEngine.Segment.Models;
using FlagsmithEngine.Trait.Models;
using Microsoft.Extensions.Logging;
using System.Threading;
using Flagsmith.Extensions;
using System.Linq;

namespace Flagsmith
{   /// <summary>
    /// A Flagsmith client.
    /// Provides an interface for interacting with the Flagsmith http API.
    /// </summary>
    /// <exception cref="FlagsmithAPIError">
    /// Thrown when error occurs during any http request to Flagsmith api.Not applicable for polling or ananlytics.
    /// </exception>
    /// <exception cref="FlagsmithClientError">
    /// A general exception with a error message. Example: Feature not found, etc.
    /// </exception>
    public class FlagsmithClient : IFlagsmithClient
    {
        private string ApiUrl { get; set; }
        private string EnvironmentKey { get; set; }
        private bool EnableClientSideEvaluation { get; set; }
        private int EnvironmentRefreshIntervalSeconds { get; set; }
        private Func<string, Flag> DefaultFlagHandler { get; set; }
        private ILogger Logger { get; set; }
        private bool EnableAnalytics { get; set; }
        private double? RequestTimeout { get; set; }
        private int? Retries { get; set; }
        private Dictionary<string, string> CustomHeaders { get; set; }
        const string _defaultApiUrl = "https://edge.api.flagsmith.com/api/v1/";
        private HttpClient httpClient;
        private EnvironmentModel Environment { get; set; }
        private readonly PollingManager _PollingManager;
        private readonly IEngine _Engine;
        private readonly AnalyticsProcessor _AnalyticsProcessor;
        /// <summary>
        /// Create flagsmith client.
        /// </summary>
        /// <param name="environmentKey">The environment key obtained from Flagsmith interface.</param>
        /// <param name="apiUrl">Override the URL of the Flagsmith API to communicate with.</param>
        /// <param name="logger">Provide logger for logging polling info & errors which is only applicable when client side evalution is enabled and analytics errors.</param>
        /// <param name="defaultFlagHandler">Callable which will be used in the case where flags cannot be retrieved from the API or a non existent feature is requested.</param>
        /// <param name="enableAnalytics">if enabled, sends additional requests to the Flagsmith API to power flag analytics charts.</param>
        /// <param name="enableClientSideEvaluation">If using local evaluation, specify the interval period between refreshes of local environment data.</param>
        /// <param name="environmentRefreshIntervalSeconds"></param>
        /// <param name="useLegacyIdentities">Enables local evaluation of flags.</param>
        /// <param name="customHeaders">Additional headers to add to requests made to the Flagsmith API</param>
        /// <param name="retries">Total http retries for every failing request before throwing the final error.</param>
        /// <param name="requestTimeout">Number of seconds to wait for a request to complete before terminating the request</param>
        public FlagsmithClient(
            string environmentKey,
            string apiUrl = _defaultApiUrl,
            ILogger logger = null,
            Func<string, Flag> defaultFlagHandler = null,
            bool enableAnalytics = false,
            bool enableClientSideEvaluation = false,
            int environmentRefreshIntervalSeconds = 60,
            Dictionary<string, string> customHeaders = null,
            int retries = 1,
            double? requestTimeout = null,
            HttpClient httpClient = null)
        {
            this.EnvironmentKey = environmentKey;
            this.ApiUrl = apiUrl;
            this.Logger = logger;
            this.DefaultFlagHandler = defaultFlagHandler;
            this.EnableAnalytics = enableAnalytics;
            this.EnableClientSideEvaluation = enableClientSideEvaluation;
            this.EnvironmentRefreshIntervalSeconds = environmentRefreshIntervalSeconds;
            this.CustomHeaders = customHeaders;
            this.Retries = retries;
            this.RequestTimeout = requestTimeout;
            this.httpClient = httpClient ?? new HttpClient();
            _Engine = new Engine();
            if (EnableAnalytics)
                _AnalyticsProcessor = new AnalyticsProcessor(this.httpClient, EnvironmentKey, ApiUrl, Logger, CustomHeaders);
            if (EnableClientSideEvaluation)
            {
                _PollingManager = new PollingManager(GetAndUpdateEnvironmentFromApi, EnvironmentRefreshIntervalSeconds);
                _ = _PollingManager.StartPoll();

            }
        }

        /// <summary>
        /// Get all the default for flags for the current environment.
        /// </summary>
        public async Task<Flags> GetEnvironmentFlags()
            => Environment != null ? GetFeatureFlagsFromDocuments() : await GetFeatureFlagsFromApi();

        /// <summary>
        /// Get all the flags for the current environment for a given identity.
        /// </summary>
        public async Task<Flags> GetIdentityFlags(string identity)
        {
            return await GetIdentityFlags(identity, null);
        }

        /// <summary>
        /// Get all the flags for the current environment for a given identity with provided traits.
        /// </summary>
        public async Task<Flags> GetIdentityFlags(string identity, List<Trait> traits)
        {
            if (Environment != null)
            {
                return GetIdentityFlagsFromDocuments(identity, traits);
            }

            return await GetIdentityFlagsFromApi(identity, traits);
        }

        public List<Segment> GetIdentitySegments(string identifier)
        {
            return GetIdentitySegments(identifier, new List<Trait>());
        }

        public List<Segment> GetIdentitySegments(string identifier, List<Trait> traits)
        {
            if (this.Environment == null)
            {
                throw new FlagsmithClientError("Local evaluation required to obtain identity segments.");
            }
            IdentityModel identityModel = new IdentityModel { Identifier = identifier, IdentityTraits = traits?.Select(t => new TraitModel { TraitKey = t.GetTraitKey(), TraitValue = t.GetTraitValue() }).ToList() };
            List<SegmentModel> segmentModels = Evaluator.GetIdentitySegments(
                this.Environment, identityModel, new List<TraitModel>()
            );

            return segmentModels?.Select(t => new Segment(id: t.Id, name: t.Name)).ToList();
        }

        private async Task<string> GetJSON(HttpMethod method, string url, string body = null)
        {
            try
            {
                var policy = HttpPolicies.GetRetryPolicyAwaitable(Retries);
                return await (await policy.ExecuteAsync(async () =>
                {
                    HttpRequestMessage request = new HttpRequestMessage(method, url)
                    {
                        Headers = {
                            { "X-Environment-Key", EnvironmentKey }
                        }
                    };
                    CustomHeaders?.ForEach(kvp => request.Headers.Add(kvp.Key, kvp.Value));
                    if (body != null)
                    {
                        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    }
                    var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(RequestTimeout ?? 100));
                    HttpResponseMessage response = await httpClient.SendAsync(request, cancellationTokenSource.Token);
                    return response.EnsureSuccessStatusCode();
                })).Content.ReadAsStringAsync();
            }
            catch (HttpRequestException e)
            {
                Logger?.LogError("\nHTTP Request Exception Caught!");
                Logger?.LogError("Message :{0} ", e.Message);
                throw new FlagsmithAPIError("Unable to get valid response from Flagsmith API");
            }
            catch (TaskCanceledException)
            {
                throw new FlagsmithAPIError("Request cancelled: Api server takes too long to respond");
            }
        }
        private async Task GetAndUpdateEnvironmentFromApi()
        {
            try
            {
                var json = await GetJSON(HttpMethod.Get, ApiUrl + "environment-document/");
                Environment = JsonConvert.DeserializeObject<EnvironmentModel>(json);
                Logger?.LogInformation("Local Environment updated: " + json);
            }
            catch (FlagsmithAPIError ex)
            {
                Logger?.LogError(ex.Message);
            }
        }
        private async Task<Flags> GetFeatureFlagsFromApi()
        {
            try
            {
                string url = ApiUrl.AppendPath("flags");
                string json = await GetJSON(HttpMethod.Get, url);
                var flags = JsonConvert.DeserializeObject<List<Flag>>(json);
                return Flags.FromApiFlag(_AnalyticsProcessor, DefaultFlagHandler, flags);
            }
            catch (FlagsmithAPIError e)
            {
                return DefaultFlagHandler != null ? Flags.FromApiFlag(_AnalyticsProcessor, DefaultFlagHandler, null) : throw e;
            }

        }
        private async Task<Flags> GetIdentityFlagsFromApi(string identity, List<Trait> traits)
        {
            try
            {
                string url = ApiUrl.AppendPath("identities");
                string jsonBody;
                string jsonResponse;

                if (traits != null && traits.Count > 0)
                {
                    jsonBody = JsonConvert.SerializeObject(new { identifier = identity, traits = traits ?? new List<Trait>() });
                    jsonResponse = await GetJSON(HttpMethod.Post, url, body: jsonBody);
                }
                else
                {
                    url += $"?identifier={identity}";
                    jsonResponse = await GetJSON(HttpMethod.Get, url);
                }

                var flags = JsonConvert.DeserializeObject<Identity>(jsonResponse)?.flags;
                return Flags.FromApiFlag(_AnalyticsProcessor, DefaultFlagHandler, flags);
            }
            catch (FlagsmithAPIError e)
            {
                return DefaultFlagHandler != null ? Flags.FromApiFlag(_AnalyticsProcessor, DefaultFlagHandler, null) : throw e;
            }

        }
        private Flags GetFeatureFlagsFromDocuments()
        {
            return Flags.FromFeatureStateModel(_AnalyticsProcessor, DefaultFlagHandler, _Engine.GetEnvironmentFeatureStates(Environment));
        }
        private Flags GetIdentityFlagsFromDocuments(string identifier, List<Trait> traits)
        {

            IdentityModel identity;

            if (traits?.Count > 0)
            {
                identity = new IdentityModel { Identifier = identifier, IdentityTraits = traits?.Select(t => new TraitModel { TraitKey = t.GetTraitKey(), TraitValue = t.GetTraitValue() }).ToList() };
            }
            else
            {
                identity = new IdentityModel { Identifier = identifier };
            }

            return Flags.FromFeatureStateModel(_AnalyticsProcessor, DefaultFlagHandler, _Engine.GetIdentityFeatureStates(Environment, identity), identity.CompositeKey);
        }
        ~FlagsmithClient() => _PollingManager?.StopPoll();
    }
}
