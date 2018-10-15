﻿using LobAccelerator.Library.Extensions;
using LobAccelerator.Library.Interfaces;
using LobAccelerator.Library.Managers.Interfaces;
using LobAccelerator.Library.Models.Common;
using LobAccelerator.Library.Models.Teams;
using LobAccelerator.Library.Models.Teams.Channels;
using LobAccelerator.Library.Models.Teams.Groups;
using LobAccelerator.Library.Models.Teams.Teams;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using static LobAccelerator.Library.Extensions.ConstantsExtension;

namespace LobAccelerator.Library.Managers
{
    public class TeamsManager
        : ITeamsManager
    {
        private readonly HttpClient httpClient;
        private readonly Uri _baseUri;
        private readonly string _apiVersion;
        private readonly ILogger logger;
        private readonly IOneDriveManager oneDriveManager;

        public TeamsManager(HttpClient httpClient, ILogger logger, IOneDriveManager oneDriveManager)
        {
            this.httpClient = httpClient;
            this.logger = logger;

            _baseUri = new Uri("https://graph.microsoft.com/");
            _apiVersion = TeamsApiVersion;

            this.oneDriveManager = oneDriveManager;
        }

        public async Task<IResult> CreateResourceAsync(TeamResource resource)
        {
            logger.LogInformation($"Starting to create the group {resource.DisplayName}");
            Result<Group> group = await CreateGroupAsync(resource);
            logger.LogInformation($"Finished creating the group {resource.DisplayName}");

            logger.LogInformation($"Starting to create the team {resource.DisplayName}");
            Result<Team> team = await CreateTeamAsync(group.Value.Id, resource);
            logger.LogInformation($"Finished creating the team {resource.DisplayName}");

            logger.LogInformation($"Starting to create {resource.Channels.Count()} channels");
            IResult channels = await CreateChannelsAsync(team.Value.Id, resource.Channels);
            logger.LogInformation($"Finished creating {resource.Channels.Count()} channels");

            logger.LogInformation($"Starting to create {resource.Members.Count()} members");
            IResult members = await AddPeopleToChannelAsync(resource.Members, team.Value.Id);
            logger.LogInformation($"Finished creating {resource.Members.Count()} members");

            logger.LogInformation($"Starting to copy files");
            IResult files = await CopyFilesToChannels(resource.Channels, team.Value.Id);
            logger.LogInformation($"Finished copying files");

            var results = Result.CombineSeparateResults(group, team, channels, members, files);
            if (results.HasError())
            {
                logger.LogError($"There was an error with the TeamsManager: {results.GetError()}");
            }
            return results;
        }

        private async Task<IResult> CopyFilesToChannels(IEnumerable<ChannelResource> channels, string teamId)
        {
            await Task.Delay(16000);
            // TODO: Remove this call.
            // BUG: Creating Teams through Graph is taking too long to propagate the files directory properties.

            var results = new List<Result<NoneResult>>();

            foreach (var channel in channels)
            {
                //TODO: Remove the following call when the bug of not creating a folder for a channel is fixed.
                await CreateChannelFolderOnGroupDocumentLibrary(teamId, channel.DisplayName);

                foreach (var resource in channel.Files)
                {
                    var result = new Result<NoneResult>();
                    try
                    {
                        if (IsFile(resource))
                        {
                            await oneDriveManager.CopyFileFromOneDriveToTeams(teamId, channel.DisplayName, resource);
                        }
                        else
                        {
                            await oneDriveManager.CopyFolderFromOneDriveToTeams(teamId, channel.DisplayName, resource);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.HasError = true;
                        result.Error = ex.Message;
                    }
                    results.Add(result);
                }
            }

            return Result.Combine(results);
        }

        private async Task CreateChannelFolderOnGroupDocumentLibrary(string teamId, string channelName)
        {
            var url = $"https://graph.microsoft.com/beta/groups/{teamId}/drive/root/children/";

            var requestBody = new
            {
                name = channelName,
                folder = new { }
            };

            var requestBodyStr = JsonConvert.SerializeObject(requestBody);
            var body = new StringContent(requestBodyStr, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(url, body);
            response.EnsureSuccessStatusCode();
        }

        private bool IsFile(string input)
            => new System.Text.RegularExpressions.Regex(@"\.[a-zA-Z0-9]*$").IsMatch(input);

        /// <summary>
        /// Creates a new group where teams will be assigned to.
        /// </summary>
        /// <returns>Group ID</returns>
        public async Task<Result<Group>> CreateGroupAsync(TeamResource resource)
        {
            var result = new Result<Group>();
            var groupUri = new Uri(_baseUri, $"{_apiVersion}/groups");

            var requestContent = new GroupBody
            {
                Description = resource.Description,
                DisplayName = resource.DisplayName,
                GroupTypes = new List<string> { GroupTypes.Unified.ToString() },
                MailEnabled = true,
                MailNickname = resource.MailNickname ?? resource.DisplayName.ToLowerInvariant().Replace(' ', '-'),
                SecurityEnabled = false
            };

            logger.LogInformation($"Creating group using {groupUri} and {JsonConvert.SerializeObject(requestContent)}");
            var response = await httpClient.PostContentAsync(groupUri.AbsoluteUri, requestContent);
            var responseString = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                result.Value = JsonConvert.DeserializeObject<Group>(responseString);
                return result;
            }

            result.HasError = true;
            result.Error = response.ReasonPhrase;
            result.DetailedError = responseString;

            return result;
        }

        /// <summary>
        /// Creates a new Team to an existing group.
        /// </summary>
        /// <returns></returns>
        public async Task<Result<Team>> CreateTeamAsync(string groupId, TeamResource resource)
        {
            var result = new Result<Team>();
            var uri = new Uri(_baseUri, $"{_apiVersion}/groups/{groupId}/team");

            var requestContent = new TeamBody
            {
                MemberSettings = resource.MemberSettings,
                MessagingSettings = resource.MessagingSettings,
                FunSettings = resource.FunSettings
            };

            var response = await httpClient.PutContentAsync(uri.AbsoluteUri, requestContent);
            var responseString = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                result.Value = JsonConvert.DeserializeObject<Team>(responseString);
                return result;
            }

            result.HasError = true;
            result.Error = response.ReasonPhrase;
            result.DetailedError = responseString;

            return result;
        }

        /// <summary>
        /// Create channels under a team.
        /// </summary>
        /// <param name="teamsId">Team ID</param>
        /// <param name="channels">List of channels to be added</param>
        /// <returns></returns>
        public async Task<IResult> CreateChannelsAsync(string teamId, IEnumerable<ChannelResource> channels)
        {
            var results = new List<IResult>();
            var uri = new Uri(_baseUri, $"{_apiVersion}/teams/{teamId}/channels");

            foreach (var channel in channels)
            {
                var result = new Result<Channel>();
                var response = await httpClient.PostContentAsync(uri.AbsoluteUri, channel);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    result.HasError = true;
                    result.Error = response.ReasonPhrase;
                    result.DetailedError = responseString;
                }

                results.Add(result);
            }

            return Result.Combine(results);
        }

        public async Task<IResult> AddTabToChannelBasedOnUrlAsync(string tabName, string serviceUrl, string teamId, string channelId)
        {
            var addTabUrl = $"{GraphAlphaApiVersion}/teams/{teamId}/channels/{channelId}/tabs";
            var result = new Result<NoneResult>();
            var quickObject = new
            {
                name = tabName,
                teamsAppId = "com.microsoft.teamspace.tab.web",
                configuration = new
                {
                    entityId = string.Empty,
                    contentUrl = serviceUrl,
                    removeUrl = string.Empty,
                    websiteUrl = serviceUrl
                }
            };

            try
            {
                var response = await httpClient.PostContentAsync(addTabUrl, quickObject);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    result.HasError = true;
                    result.Error = response.ReasonPhrase;
                    result.DetailedError = responseString;
                }
            }
            catch (Exception ex)
            {
                result.HasError = true;
                result.Error = ex.Message;
                result.DetailedError = JsonConvert.SerializeObject(ex);
            }

            return result;
        }


        public async Task<IResult> AddPeopleToChannelAsync(IEnumerable<string> members, string teamId)
        {
            var results = new List<Result<NoneResult>>();
            var addMemberUrl = $"{_apiVersion}/groups/{teamId}/members/$ref";

            foreach (var member in members)
            {
                var result = new Result<NoneResult>();
                try
                {
                    var addMemberBody = new AddGroupMemberBody(member);
                    var response = await httpClient.PostContentAsync(addMemberUrl, addMemberBody);
                    var responseString = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        result.HasError = true;
                        result.Error = response.ReasonPhrase;
                        result.DetailedError = responseString;
                    }
                }
                catch (Exception ex)
                {
                    result.HasError = true;
                    result.Error = ex.Message;
                    result.DetailedError = JsonConvert.SerializeObject(ex);
                }

                results.Add(result);
            }

            return Result.Combine(results);
        }

        public async Task<string> SearchTeamAsync(string displayName)
        {
            var listTeams = new Uri(_baseUri, $"beta/groups?$filter=displayName eq '{displayName}'&$select=id");

            var response = await httpClient.GetAsync(listTeams);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var contentObj = JObject.Parse(content);

            return contentObj["value"][0]["id"].Value<string>();
        }

        public async Task<Result<NoneResult>> DeleteChannelAsync(string groupId)
        {
            var result = new Result<NoneResult>();
            var deleteUri = new Uri(_baseUri, $"{_apiVersion}/groups/{groupId}");
            var deletePermanentUri = new Uri(_baseUri, $"{_apiVersion}/directory/deleteditems/microsoft.graph.group/{groupId}");

            var responseDelete = await httpClient.DeleteAsync(deleteUri);
            responseDelete.EnsureSuccessStatusCode();

            var responseDeletePerm = await httpClient.DeleteAsync(deletePermanentUri);
            responseDeletePerm.EnsureSuccessStatusCode();

            return result;
        }
    }
}
