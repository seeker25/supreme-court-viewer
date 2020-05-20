﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JCCommon.Clients.LookupServices;
using LazyCache;
using Microsoft.Extensions.Configuration;
using Scv.Api.Helpers;
using Scv.Api.Helpers.ContractResolver;
using Scv.Api.Helpers.Exceptions;
using CodeLookup = System.Collections.Generic.ICollection<JCCommon.Clients.LookupServices.LookupCode>;

namespace Scv.Api.Services
{
    /// <summary>
    /// This should handle caching and LookupServicesClient.
    /// </summary>
    public class LookupService
    {
        #region Variables
        private readonly IAppCache _cache;
        private readonly IConfiguration _configuration;
        private readonly LookupServiceClient _lookupClient;
        #endregion

        #region Properties
        private DateTimeOffset CacheExpiry => DateTimeOffset.Now.AddHours(1);
        #endregion

        #region Constructor
        public LookupService(IConfiguration configuration, LookupServiceClient lookupClient, IAppCache cache)
        {
            _configuration = configuration;
            _lookupClient = lookupClient;
            _cache = cache;
            SetupLookupServicesClient();
        }
        #endregion

        #region Collection methods
        public async Task<CodeLookup> GetCriminalAppearanceReasons() => await GetDataFromCache("CriminalAppearanceReasons", async () => await _lookupClient.CodesCriminalAppearanceReasonsAsync());
        public async Task<CodeLookup> GetCriminalAppearanceResults() => await GetDataFromCache("CriminalAppearanceResults", async () => await _lookupClient.CodesCriminalAppearanceResultsAsync());
        public async Task<CodeLookup> GetFindings() => await GetDataFromCache("Findings", async() => await _lookupClient.CodesFindingsAsync());
        public async Task<CodeLookup> GetCourtClass() => await GetDataFromCache("CourtClasses", async () => await _lookupClient.CodesCourtClassesAsync());
        public async Task<CodeLookup> GetCourtLevel() => await GetDataFromCache("CourtLevels", async () => await _lookupClient.CodesCourtLevelsAsync());
        public async Task<CodeLookup> GetCriminalAssets() => await GetDataFromCache("CriminalAssets", async() => await _lookupClient.CodesCriminalAssetsAsync());
        public async Task<CodeLookup> GetDocuments() => await GetDataFromCache("Documents", async () => await _lookupClient.CodesDocumentsAsync());
        public async Task<CodeLookup> GetRoles() => await GetDataFromCache("Roles", async () => await _lookupClient.CodesRolesAsync());
        public async Task<CodeLookup> GetParticipantRoles() => await GetDataFromCache("ParticipantRoles", async () => await _lookupClient.CodesParticipantRolesAsync());
        #endregion

        #region Lookup Methods
        public async Task<string> GetCriminalAssetsDescriptions(string code) => FindLongDescriptionFromCode(await GetCriminalAssets(), code);
        public async Task<string> GetCriminalAppearanceReasonsDescription(string code) => FindShortDescriptionFromCode(await GetCriminalAppearanceReasons(), code);
        public async Task<string> GetCriminalAppearanceResultsDescription(string code) => FindShortDescriptionFromCode(await GetCriminalAppearanceResults(), code);
        public async Task<string> GetFindingDescription(string code) => FindShortDescriptionFromCode(await GetFindings(), code);
        public async Task<string> GetActivityClassCd(string code) => FindLongDescriptionFromCode(await GetCourtClass(), code);
        public async Task<string> GetCourtClassDescription(string code) => FindShortDescriptionFromCode(await GetCourtClass(), code);
        public async Task<string> GetCourtLevelDescription(string code) => FindShortDescriptionFromCode(await GetCourtLevel(), code);
        public async Task<string> GetCivilRoleTypeDescription(string code) => FindShortDescriptionFromCode(await GetRoles(), code);
        public async Task<string> GetCriminalParticipantRoleDescription(string code) => FindLongDescriptionFromCode(await GetParticipantRoles(), code);
        public async Task<string> GetDocumentDescriptionAsync(string code) => FindShortDescriptionFromCode(await GetDocuments(), code);

        /// <summary>
        /// Reads from the configuration for the document category.
        /// </summary>
        /// <param name="documentCode"></param>
        /// <returns>string</returns>
        public string GetDocumentCategory(string documentCode)
        {
            var configurationSections =
                _configuration.GetSection("DocumentCategories").Get<Dictionary<string, string>>() ??
                throw new ConfigurationException("Couldn't not build dictionary based on DocumentCategories");
            return configurationSections.FirstOrDefault(cs => cs.Value.Split(",").Contains(documentCode)).Key ?? "";
        }
        #endregion

        #region Helpers
        private async Task<T> GetDataFromCache<T>(string key, Func<Task<T>> fetchFunction)
        {
            return await _cache.GetOrAddAsync(key,
                async () => await fetchFunction.Invoke(), CacheExpiry);
        }

        private string FindShortDescriptionFromCode(CodeLookup lookupCodes, string code) => lookupCodes.FirstOrDefault(lookupCode => lookupCode.Code == code)?.ShortDesc ?? "";
        private string FindLongDescriptionFromCode(CodeLookup lookupCodes, string code) => lookupCodes.FirstOrDefault(lookupCode => lookupCode.Code == code)?.LongDesc ?? "";

        private void SetupLookupServicesClient()
        {
            _lookupClient.JsonSerializerSettings.ContractResolver = new SafeContractResolver();
            _lookupClient.BaseUrl = _configuration.GetNonEmptyValue("LookupServicesClient:Url");
        }
        #endregion
    }
}