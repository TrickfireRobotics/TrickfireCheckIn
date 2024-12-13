﻿using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Notion.Client;
using System.Net;
using System.Text.RegularExpressions;
using TrickFireDiscordBot.Notion;

namespace TrickFireDiscordBot
{
    public class RoleSyncer(ILogger<RoleSyncer> logger, INotionClient notionClient, DiscordClient discordClient, WebhookListener listener)
    {
        public const string WebhookEndpoint = "/members";

        private static readonly Regex _technicalLeadRegex = new(Config.Instance.TechnicalLeadRegex);

        public ILogger Logger { get; } = logger;
        public INotionClient NotionClient { get; } = notionClient;
        public DiscordClient DiscordClient { get; } = discordClient;
        public WebhookListener WebhookListener { get; } = listener;

        private DiscordGuild? _trickFireGuild;
        private readonly Dictionary<string, DiscordRole> _discordRoleCache = [];
        private string? _teamPageNamePropertyId = null;

        public async Task Start()
        {
            WebhookListener.OnWebhookReceived += OnWebhook;

            _trickFireGuild = await DiscordClient.GetGuildAsync(Config.Instance.TrickfireGuildId);
            foreach (DiscordRole role in _trickFireGuild.Roles.Values)
            {
                _discordRoleCache.Add(role.Name, role);
            }
        }

        public void Stop()
        {
            WebhookListener.OnWebhookReceived -= OnWebhook;
        }

        public async Task SyncAllMemberRoles(bool dryRun = true)
        {
            if (_trickFireGuild is null)
            {
                return;
            }

            Task<PaginatedList<Page>> nextPageQuery(string? cursor) =>
                NotionClient.Databases.QueryAsync(
                    Config.Instance.MembersDatabaseId,
                    new DatabasesQueryParameters()
                    {
                        StartCursor = cursor
                    }
                );

            HashSet<DiscordMember?> processedMembers = [];
            await foreach (Page page in PaginatedListHelper.GetEnumerable(nextPageQuery))
            {
                await Task.Delay(333);
                processedMembers.Add(await SyncRoles(page, dryRun));
                Logger.LogInformation("\n");
            }

            Logger.LogInformation("\nNo notion page users:");
            DiscordRole inactiveRole = _discordRoleCache.Values.First(role => role.Id == Config.Instance.InactiveRoleId);
            await foreach (DiscordMember member in _trickFireGuild.GetAllMembersAsync())
            {
                if (processedMembers.Contains(member))
                {
                    continue;
                }
                if (!dryRun && !member.Roles.Contains(inactiveRole))
                {
                    await member.GrantRoleAsync(inactiveRole);
                    await Task.Delay(333);
                }
                Logger.LogInformation("{}, ({})", member.DisplayName, member.Username);
            }
        }

        private void OnWebhook(HttpListenerRequest request)
        {
            if (request.RawUrl == null || !request.RawUrl.StartsWith(WebhookEndpoint))
            {
                return;
            }

            using StreamReader reader = new(request.InputStream);
            string json = reader.ReadToEnd();
            Automation? automation = JsonConvert.DeserializeObject<Automation>(json);
            if (automation == null || automation.Data is not Page page)
            {
                Logger.LogWarning("Could not parse automation: {}", json);
                return;
            }

            SyncRoles(page).GetAwaiter().GetResult();

            Console.WriteLine(JsonConvert.SerializeObject(page, Formatting.Indented));
        }

        private async Task<DiscordMember?> SyncRoles(Page notionPage, bool dryRun = true)
        {
            if (_trickFireGuild is null)
            {
                return null;
            }

            DiscordMember? member = await GetMember(notionPage);
            if (member is null)
            {
                return null;
            }

            IEnumerable<DiscordRole> newRoles = await GetRoles(notionPage);
            Logger.LogInformation(member.DisplayName);
            foreach (DiscordRole role in newRoles)
            {
                Logger.LogInformation(role!.Name);
            }

            if (!dryRun) 
            {
                DiscordMember botMember = await _trickFireGuild.GetMemberAsync(DiscordClient.CurrentUser.Id, true);
                int highestRole = botMember.Roles.Max(role => role.Position);
                await member.ModifyAsync(model =>
                {
                    List<DiscordRole> rolesWithLeadership = new(newRoles);
                    foreach (DiscordRole role in member.Roles.Where(role => role.Position >= highestRole))
                    {
                        rolesWithLeadership.Add(role);
                    }
                    model.Roles = rolesWithLeadership;
                });
                await Task.Delay(333);
            }

            return member;
        }

        private async Task<DiscordMember?> GetMember(Page notionPage)
        {
            if (_trickFireGuild is null)
            {
                return null;
            }

            // We want this to fail hard if something is wrong
            string? username = (notionPage.Properties[Config.Instance.DiscordUsernamePropertyName] 
                as PhoneNumberPropertyValue)!.PhoneNumber;

            if (string.IsNullOrWhiteSpace(username))
            {
                Logger.LogWarning("User with page url {} has no discord.", notionPage.Url);
                return null;
            }

            username = username.TrimStart('@').ToLower();

            DiscordMember? member = _trickFireGuild.Members.Values
                .FirstOrDefault(member => member.Username == username);

            if (member is null)
            {
                IReadOnlyList<DiscordMember> search = await _trickFireGuild.SearchMembersAsync(username);
                if (search.Count == 0 || search[0].Username != username)
                {
                    Logger.LogWarning("Could not find member: {}", username);
                }
                else
                {
                    member = search[0];
                }
                await Task.Delay(333);
            }
            return member;
        }

        private async Task<IEnumerable<DiscordRole>> GetRoles(Page notionPage)
        {
            List<DiscordRole> roles = [];

            DiscordRole? activeRole = GetActiveRole(notionPage);
            if (activeRole is not null)
            {
                roles.Add(activeRole);
            }

            roles.AddRange(await GetTeams(notionPage));

            MultiSelectPropertyValue positions = (notionPage.Properties[Config.Instance.ClubPositionsPropertyName]
                as MultiSelectPropertyValue)!;
            foreach (SelectOption item in positions.MultiSelect)
            {
                if (item.Name == "Individual Contributor")
                {
                    continue;
                }

                if (_technicalLeadRegex.IsMatch(item.Name))
                {
                    roles.Add(_discordRoleCache.Values.First(role => role.Id == Config.Instance.TechnicalLeadRoleId));
                }

                // Remove team suffix to make roles a little easier to read
                DiscordRole? positionRole = GetRoleOrDefault(item.Name.Replace(" Team", ""));
                if (positionRole is not null)
                {
                    roles.Add(positionRole);
                }
            }

            return roles;
        }

        private DiscordRole? GetActiveRole(Page notionPage)
        {
            string activeValue = (notionPage.Properties[Config.Instance.ActivePropertyName] 
                as SelectPropertyValue)!.Select.Name;
            if (activeValue == "Active")
            {
                return null;
            }
            else
            {
                return GetRoleOrDefault(activeValue);
            }
        }

        private async Task<IEnumerable<DiscordRole>> GetTeams(Page notionPage)
        {
            List<DiscordRole> roles = [];
            List<ObjectId> teamIds = (notionPage.Properties[Config.Instance.TeamsPropertyName]
                as RelationPropertyValue)!.Relation;
            foreach (ObjectId id in teamIds)
            {
                string teamName = await GetTeamName(id);

                // Remove team suffix to make roles a little easier to read
                DiscordRole? role = GetRoleOrDefault(teamName.Replace(" Team", ""));
                if (role is not null)
                {
                    roles.Add(role);
                }

            }

            return roles;
        }

        private async Task<string> GetTeamName(ObjectId id)
        {
            // Try using our cached property id if it exists
            if (_teamPageNamePropertyId is not null)
            {
                try
                {
                    ListPropertyItem item = (await NotionClient.Pages.RetrievePagePropertyItemAsync(new RetrievePropertyItemParameters()
                    {
                        PageId = id.Id,
                        PropertyId = _teamPageNamePropertyId
                    }) as ListPropertyItem)!;
                    return (item.Results.First() as TitlePropertyItem)!.Title.PlainText;
                }
                catch (NotionApiException ex)
                {
                    // If not found, then move on to rest of method
                    // If another error, rethrow the error to move it up the
                    // stack
                    if (ex.NotionAPIErrorCode != NotionAPIErrorCode.ObjectNotFound)
                    {
                        throw;
                    }
                }
            }

            // If it doesn't or fails, then recache it and get the whole page
            Page teamPage = await NotionClient.Pages.RetrieveAsync(id.Id);
            TitlePropertyValue nameValue = (teamPage.Properties[Config.Instance.TeamNamePropertyName]
                as TitlePropertyValue)!;
            _teamPageNamePropertyId = nameValue.Id;
            return nameValue.Title[0].PlainText;
        }

        private DiscordRole? GetRoleOrDefault(string roleName)
        {
            if (!_discordRoleCache.TryGetValue(roleName, out DiscordRole? role))
            {
                Logger.LogWarning("Could not find role with name: {}", roleName);
            }

            return role;
        }
    }
}
