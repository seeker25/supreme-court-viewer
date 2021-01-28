﻿using JCCommon.Clients.FileServices;
using LazyCache;
using MapsterMapper;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Serialization;
using Scv.Api.Helpers;
using Scv.Api.Helpers.ContractResolver;
using Scv.Api.Helpers.Extensions;
using Scv.Api.Models.Civil.CourtList;
using Scv.Api.Models.Criminal.CourtList;
using Scv.Api.Models.Criminal.Detail;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Scv.Api.Services
{
    public class CourtListService
    {
        #region Variables

        private readonly FileServicesClient _filesClient;
        private readonly LookupService _lookupService;
        private readonly LocationService _locationService;
        private readonly IAppCache _cache;
        private readonly IMapper _mapper;
        private readonly string _requestApplicationCode;
        private readonly string _requestAgencyIdentifierId;
        private readonly string _requestPartId;

        #endregion Variables

        #region Constructor

        public CourtListService(IConfiguration configuration, FileServicesClient filesClient, IMapper mapper, LookupService lookupService, LocationService locationService, IAppCache cache, ClaimsPrincipal user)
        {
            _filesClient = filesClient;
            _filesClient.JsonSerializerSettings.ContractResolver = new SafeContractResolver { NamingStrategy = new CamelCaseNamingStrategy() };
            _cache = cache;
            _cache.DefaultCachePolicy.DefaultCacheDurationSeconds = int.Parse(configuration.GetNonEmptyValue("Caching:FileExpiryMinutes")) * 60;
            _mapper = mapper;

            _lookupService = lookupService;
            _locationService = locationService;
            _requestApplicationCode = configuration.GetNonEmptyValue("Request:ApplicationCd");
            _requestAgencyIdentifierId = user.AgencyCode();
            _requestPartId = user.ParticipantId();
        }

        #endregion Constructor

        public async Task<Models.CourtList.CourtList> CourtListAsync(string agencyId, string roomCode, DateTime proceeding, string divisionCode, string fileNumber)
        {
            var proceedingDateString = proceeding.ToString("yyyy-MM-dd");
            var agencyCode = await _locationService.GetLocationCodeFromId(agencyId);

            async Task<CourtList> CourtList() => await _filesClient.FilesCourtlistAsync(agencyId, roomCode, proceedingDateString, divisionCode, fileNumber);
            async Task<CourtCalendarDetailByDay> CourtCalendarDetailByDay() =>
                await _filesClient.FilesCourtcalendardetailsbydayAsync(_requestAgencyIdentifierId, _requestPartId, agencyCode,
                    proceeding.ToString("yyyy-MM-dd HH:mm:ss.S"), roomCode);

            var courtCalendarDetailsTask = _cache.GetOrAdd($"CourtCalendarDetails-{agencyId}-{roomCode}-{proceedingDateString}-{fileNumber}-{_requestPartId}",
                    CourtCalendarDetailByDay);
            var originalCourtListTask = _cache.GetOrAddAsync($"CourtList-{agencyId}-{roomCode}-{proceedingDateString}-{fileNumber}-{_requestPartId}", CourtList);

            //Query by courtCalendarDetails, because it returns quite a bit faster than the courtList.
            var courtCalendarDetails = await courtCalendarDetailsTask;

            var civilCourtCalendarAppearances = courtCalendarDetails?.Appearance
                .WhereToList(app => app.CourtDivisionCd == CourtCalendarDetailAppearanceCourtDivisionCd.I);
            var criminalCourtCalendarAppearances = courtCalendarDetails?.Appearance
                .WhereToList(app => app.CourtDivisionCd == CourtCalendarDetailAppearanceCourtDivisionCd.R);

            var civilFileIds = civilCourtCalendarAppearances.SelectToList(ccl => ccl.PhysicalFileId);
            var criminalFileIds = criminalCourtCalendarAppearances.SelectToList(ccl => ccl.MdocJustinNo);

            if (civilFileIds.Count == 0 && criminalFileIds.Count == 0)
                return new Models.CourtList.CourtList();

            //Start file details tasks.
            var civilFileDetailTasks = CivilFileDetailTasks(civilFileIds);
            var criminalFileDetailTasks = CriminalFileDetailTasks(criminalFileIds);

            //Start appearances tasks.
            var civilAppearanceTasks = CivilAppearancesTasks(proceeding, civilFileIds);
            var criminalAppearanceTasks = CriminalAppearancesTasks(proceeding, criminalFileIds);

            //Start file content tasks. 
            var civilFileContentTasks = CivilFileContentTasks(civilFileIds);

            //Await our asynchronous requests.
            var civilFileDetails = (await civilFileDetailTasks.WhenAll()).ToList();
            var criminalFileDetails = (await criminalFileDetailTasks.WhenAll()).ToList();
            var civilAppearances = (await civilAppearanceTasks.WhenAll()).ToList();
            var criminalAppearances = (await criminalAppearanceTasks.WhenAll()).ToList();
            var civilFileContents = (await civilFileContentTasks.WhenAll()).ToList();
            var originalCourtList = await originalCourtListTask;

            //Note, there is test data that doesn't have a CourtList, but may have CourtCalendarDetails. 
            //If this is the case with real data, or becomes an issue with testing, I'll have to 
            //create a blank courtList object and overlay CourtCalendarDetails on top of it.
            //Some overlap happens already, but some of the fields aren't passed over (they already exist in CourtList). 
            //PCSS for example isn't aware of CourtList, it only uses CourtCalendarDetails where as SCV uses the CourtList + CourtCalendarDetails. 
            var courtList = _mapper.Map<Models.CourtList.CourtList>(originalCourtList);

            courtList.CivilCourtList = await PopulateCivilCourtListFromFileDetails(courtList.CivilCourtList, civilFileDetails);
            courtList.CriminalCourtList = await PopulateCriminalCourtListFromFileDetails(courtList.CriminalCourtList, criminalFileDetails);

            courtList.CivilCourtList = await PopulateCivilCourtListFromAppearances(courtList.CivilCourtList, civilAppearances);
            courtList.CriminalCourtList = await PopulateCriminalCourtListFromAppearances(courtList.CriminalCourtList, criminalAppearances);
            
            courtList.CivilCourtList = PopulateCivilCourtListFromCourtCalendarDetails(courtList.CivilCourtList, civilCourtCalendarAppearances);
            courtList.CriminalCourtList = PopulateCriminalCourtListFromCourtCalendarDetails(courtList.CriminalCourtList, criminalCourtCalendarAppearances);

            courtList.CivilCourtList = PopulateCivilCourtListFromFileContent(courtList.CivilCourtList, civilFileContents);
            return courtList;
        }

        #region Helpers

        #region Fetching Methods

        #region Civil
        private List<Task<CivilFileDetailResponse>> CivilFileDetailTasks(List<string> fileIds)
        {
            var fileDetailTasks = new List<Task<CivilFileDetailResponse>>();
            foreach (var fileId in fileIds)
            {
                async Task<CivilFileDetailResponse> FileDetails() => await _filesClient.FilesCivilGetAsync(_requestAgencyIdentifierId, _requestPartId, _requestApplicationCode, fileId);
                fileDetailTasks.Add(_cache.GetOrAddAsync($"CivilFileDetail-{fileId}-{_requestPartId}", FileDetails));
            }

            return fileDetailTasks;
        }

        private List<Task<CivilFileAppearancesResponse>> CivilAppearancesTasks(DateTime? proceeding, List<string> fileIds)
        {
            var targetDateInPast = DateTime.Now > proceeding;
            var lookForPastAppearances = targetDateInPast ? HistoryYN2.Y : HistoryYN2.N;
            var lookForFutureAppearances = targetDateInPast ? FutureYN2.N : FutureYN2.Y;

            var appearanceTasks = new List<Task<CivilFileAppearancesResponse>>();
            foreach (var fileId in fileIds)
            {
                async Task<CivilFileAppearancesResponse> Appearances() => await _filesClient.FilesCivilAppearancesAsync(_requestAgencyIdentifierId, _requestPartId, lookForFutureAppearances,
                    lookForPastAppearances, fileId);
                appearanceTasks.Add(_cache.GetOrAddAsync($"CivilAppearances-{fileId}-InPast-{targetDateInPast}-{_requestPartId}", Appearances));
            }

            return appearanceTasks;
        }

        private List<Task<CivilFileContent>> CivilFileContentTasks(List<string> fileIds)
        {
            var fileContentTasks = new List<Task<CivilFileContent>>();
            foreach (var fileId in fileIds)
            {
                async Task<CivilFileContent> FileContent() =>
                    await _filesClient.FilesCivilFilecontentAsync(null, null, null, null, fileId, _requestApplicationCode);
                fileContentTasks.Add(_cache.GetOrAddAsync($"CivilFileContent-{fileId}-{_requestPartId}", FileContent));
            }

            return fileContentTasks;
        }
        #endregion Civil

        #region Criminal 
        private List<Task<CriminalFileDetailResponse>> CriminalFileDetailTasks(List<string> fileIds)
        {
            var fileDetailTasks = new List<Task<CriminalFileDetailResponse>>();
            foreach (var fileId in fileIds)
            {
                async Task<CriminalFileDetailResponse> FileDetails() =>
                    await _filesClient.FilesCriminalGetAsync(_requestAgencyIdentifierId, _requestPartId, _requestApplicationCode,
                        fileId);
                fileDetailTasks.Add(_cache.GetOrAddAsync($"CriminalFileDetail-{fileId}-{_requestPartId}", FileDetails));
            }

            return fileDetailTasks;
        }

        private List<Task<CriminalFileAppearancesResponse>> CriminalAppearancesTasks(DateTime? proceeding, List<string> fileIds)
        {
            var targetDateInPast = DateTime.Now > proceeding;
            var lookForPastAppearances = targetDateInPast ? HistoryYN.Y : HistoryYN.N;
            var lookForFutureAppearances = targetDateInPast ? FutureYN.N : FutureYN.Y;

            var appearanceTasks = new List<Task<CriminalFileAppearancesResponse>>();
            foreach (var fileId in fileIds)
            {
                async Task<CriminalFileAppearancesResponse> Appearances() => await _filesClient.FilesCriminalAppearancesAsync(
                    _requestAgencyIdentifierId, _requestPartId, lookForFutureAppearances,
                    lookForPastAppearances, fileId);
                appearanceTasks.Add(_cache.GetOrAddAsync($"CriminalAppearances-{fileId}-InPast-{targetDateInPast}-{_requestPartId}", Appearances));
            }

            return appearanceTasks;
        }
        #endregion Criminal

        #endregion Fetching Methods

        #region Populating Methods

        #region Civil
        private async Task<ICollection<CivilCourtList>> PopulateCivilCourtListFromFileDetails(ICollection<CivilCourtList> courtList, ICollection<CivilFileDetailResponse> fileDetails)
        {
            foreach (var courtListFile in courtList)
            {
                var fileId = courtListFile.PhysicalFile.PhysicalFileID;
                var fileDetail = fileDetails.FirstOrDefault(x => x.PhysicalFileId == fileId);
                courtListFile.ActivityClassCd = await _lookupService.GetActivityClassCdLong(fileDetail?.CourtClassCd.ToString());
                courtListFile.ActivityClassDesc = await _lookupService.GetActivityClassCdShort(fileDetail?.CourtClassCd.ToString());
                courtListFile.CommentToJudgeText = fileDetail?.CommentToJudgeTxt;
                courtListFile.TrialRemarkTxt = fileDetail?.TrialRemarkTxt;

                foreach (var hearingRestriction in courtListFile.HearingRestriction)
                {
                    hearingRestriction.HearingRestrictionTypeDesc = await _lookupService.GetHearingRestrictionDescription(hearingRestriction.HearingRestrictiontype);
                }
                courtListFile.Document = PopulateCivilDocuments(courtListFile.Document);
            }

            return courtList;
        }
        private async Task<ICollection<CivilCourtList>> PopulateCivilCourtListFromAppearances(ICollection<CivilCourtList> courtList, ICollection<CivilFileAppearancesResponse> civilAppearances)
        {
            foreach (var courtListFile in courtList)
            {
                var targetAppearanceResponse = civilAppearances.FirstOrDefault(ca => ca.ApprDetail.Any(ad => ad.AppearanceId == courtListFile.AppearanceId));
                var targetAppearance = targetAppearanceResponse?.ApprDetail.FirstOrDefault(ad => ad.AppearanceId == courtListFile.AppearanceId);
                if (targetAppearance != null)
                {
                    courtListFile.AppearanceReasonCd = targetAppearance?.AppearanceReasonCd;
                    courtListFile.AppearanceReasonDesc =
                        await _lookupService.GetCivilAppearanceReasonsDescription(targetAppearance?.AppearanceReasonCd);

                    courtListFile.AppearanceStatusCd = targetAppearance?.AppearanceStatusCd.ToString();
                    courtListFile.OutOfTownJudge = targetAppearance.OutOfTownJudgeTxt;
                    courtListFile.SecurityRestriction = targetAppearance.SecurityRestrictionTxt;
                    courtListFile.SupplementalEquipment = targetAppearance.SupplementalEquipmentTxt;
                    courtListFile.JudgeInitials = targetAppearance.JudgeInitials;
                    courtListFile.EstimatedTimeHour = targetAppearance.EstimatedTimeHour?.ReturnNullIfEmpty();
                    courtListFile.EstimatedTimeMin = targetAppearance.EstimatedTimeMin?.ReturnNullIfEmpty();
                }
            }

            return courtList;
        }


        private ICollection<CivilClDocument> PopulateCivilDocuments(ICollection<CivilClDocument> documents)
        {
            foreach (var document in documents)
            {
                document.Category = _lookupService.GetDocumentCategory(document.DocumentTypeCd);
            }
            return documents;
        }

        private ICollection<CivilCourtList> PopulateCivilCourtListFromFileContent(ICollection<CivilCourtList> courtList,
            ICollection<CivilFileContent> fileContents)
        {
            foreach (var courtListFile in courtList)
            {
                var fileId = courtListFile.PhysicalFile.PhysicalFileID;
                var civilFile = fileContents.Where(fc => fc != null).FirstOrDefault(fc => fc.PhysicalFileId == fileId)?.CivilFile
                    .FirstOrDefault(cf => cf.PhysicalFileID == fileId);

                //This doesn't map correctly. the WSDL states CFCSAFileYN, but the server returns cfcsafileYN.
                if (civilFile != null && civilFile.AdditionalProperties.ContainsKey("cfcsafileYN") && civilFile.AdditionalProperties["cfcsafileYN"] != null)
                {
                    courtListFile.CfcsaFile = civilFile.AdditionalProperties["cfcsafileYN"].Equals("Y");
                }

                courtListFile.FileCommentText = civilFile?.FileCommentText;
            }

            return courtList;
        }

        private ICollection<CivilCourtList> PopulateCivilCourtListFromCourtCalendarDetails(ICollection<CivilCourtList> courtList,
            ICollection<CourtCalendarDetailAppearance> courtCalendarDetailAppearances)
        {
            foreach (var courtListFile in courtList)
            {
                var courtCalendarDetailAppearance = courtCalendarDetailAppearances.FirstOrDefault(cda => cda.AppearanceId == courtListFile.AppearanceId);
                courtListFile.Video = courtCalendarDetailAppearance?.VideoYn == CourtCalendarDetailAppearanceVideoYn.Y;
                courtListFile.RemoteVideo = courtCalendarDetailAppearance?.RemoteVideoYn == CourtCalendarDetailAppearanceRemoteVideoYn.Y;
                courtListFile.StyleOfCause = courtCalendarDetailAppearance?.StyleOfCause;
            }

            return courtList;
        }
        #endregion

        #region Criminal

        private async Task<ICollection<CriminalCourtList>> PopulateCriminalCourtListFromFileDetails(ICollection<CriminalCourtList> courtList,
        ICollection<CriminalFileDetailResponse> fileDetails)
        {
            foreach (var courtListFile in courtList)
            {
                var fileDetail = fileDetails.FirstOrDefault(x => x.JustinNo == courtListFile.FileInformation.MdocJustinNo);
                courtListFile.ActivityClassCd = await _lookupService.GetActivityClassCdLong(fileDetail?.CourtClassCd.ToString());
                courtListFile.ActivityClassDesc = await _lookupService.GetActivityClassCdShort(fileDetail?.CourtClassCd.ToString());
                courtListFile.Crown = PopulateCrown(fileDetail);
                courtListFile.TrialRemark = fileDetail?.TrialRemark;
                courtListFile.TrialRemarkTxt = fileDetail?.TrialRemarkTxt;
                var participant = fileDetail?.Participant.FirstOrDefault(p => p.PartId == courtListFile.FileInformation.PartId);
                if (participant != null)
                {
                    courtListFile.InCustody = participant.InCustodyYN.HasValue && participant.InCustodyYN == CriminalParticipantInCustodyYN.Y;
                    courtListFile.Detained = participant.DetainedYN.HasValue && participant.DetainedYN == CriminalParticipantDetainedYN.Y;
                }

                foreach (var hearingRestriction in courtListFile.HearingRestriction)
                {
                    hearingRestriction.HearingRestrictionTypeDesc = await _lookupService.GetHearingRestrictionDescription(hearingRestriction.HearingRestrictiontype);
                }
            }

            return courtList;
        }

        private async Task<ICollection<CriminalCourtList>> PopulateCriminalCourtListFromAppearances(ICollection<CriminalCourtList> courtList, ICollection<CriminalFileAppearancesResponse> criminalAppearances)
        {
            foreach (var courtListFile in courtList)
            {
                var targetAppearanceResponse = criminalAppearances.FirstOrDefault(ca =>
                    ca.ApprDetail.Any(ad => ad.AppearanceId == courtListFile.CriminalAppearanceID));
                var targetAppearance =
                    targetAppearanceResponse?.ApprDetail.FirstOrDefault(ad => ad.AppearanceId == courtListFile.CriminalAppearanceID);
                if (targetAppearance != null)
                {
                    courtListFile.AppearanceReasonCd = targetAppearance?.AppearanceReasonCd;
                    courtListFile.AppearanceReasonDesc = await _lookupService.GetCriminalAppearanceReasonsDescription(targetAppearance?.AppearanceReasonCd);
                    courtListFile.AppearanceStatusCd = targetAppearance?.AppearanceStatusCd.ToString();
                    courtListFile.OutOfTownJudge = targetAppearance.OutOfTownJudgeTxt;
                    courtListFile.SecurityRestriction = targetAppearance.SecurityRestrictionTxt;
                    courtListFile.SupplementalEquipment = targetAppearance.SupplementalEquipmentTxt;
                    courtListFile.JudgeInitials = targetAppearance.JudgeInitials;
                    courtListFile.EstimatedTimeHour = targetAppearance.EstimatedTimeHour?.ReturnNullIfEmpty();
                    courtListFile.EstimatedTimeMin = targetAppearance.EstimatedTimeMin?.ReturnNullIfEmpty();
                }
            }

            return courtList;
        }

        private ICollection<CrownWitness> PopulateCrown(CriminalFileDetailResponse fileDetail)
        {
            if (fileDetail == null)
                return null;

            var crown =
                _mapper.Map<ICollection<CrownWitness>>(fileDetail.Witness.Where(w => w.RoleTypeCd == CriminalWitnessRoleTypeCd.CRN)
                    .ToList());
            foreach (var crownWitness in crown)
            {
                crownWitness.Assigned = crownWitness.IsAssigned(fileDetail.AssignedPartNm);
            }

            return crown;
        }

        private ICollection<CriminalCourtList> PopulateCriminalCourtListFromCourtCalendarDetails(ICollection<CriminalCourtList> courtList,
            ICollection<CourtCalendarDetailAppearance> courtCalendarDetailAppearances)
        {
            foreach (var courtListFile in courtList)
            {
                var courtCalendarDetailAppearance = courtCalendarDetailAppearances.FirstOrDefault(cda => cda.ApprId == courtListFile.CriminalAppearanceID);
                courtListFile.Video = courtCalendarDetailAppearance?.VideoYn == CourtCalendarDetailAppearanceVideoYn.Y;
                courtListFile.RemoteVideo = courtCalendarDetailAppearance?.RemoteVideoYn == CourtCalendarDetailAppearanceRemoteVideoYn.Y;
                courtListFile.CaseAgeDaysNumber = courtCalendarDetailAppearance?.CaseAgeDays;
                courtListFile.AdjudicatorNm = courtCalendarDetailAppearance?.AdjudicatorNm;
                courtListFile.StyleOfCause = courtCalendarDetailAppearance?.StyleOfCause;
            }

            return courtList;
        }

        #endregion Criminal

        #endregion Populating Methods

        #endregion Helpers
    }
}