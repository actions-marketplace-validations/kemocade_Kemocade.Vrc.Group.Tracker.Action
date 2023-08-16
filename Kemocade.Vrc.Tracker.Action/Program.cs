using CommandLine;
using Kemocade.Vrc.Group.Tracker.Action;
using OtpNet;
using System.Text.Json;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;
using static Kemocade.Vrc.Group.Tracker.Action.RolePermission;
using static System.Console;
using static System.IO.File;
using static System.Text.Json.JsonSerializer;

// Configure Cancellation
using CancellationTokenSource tokenSource = new();
CancelKeyPress += delegate { tokenSource.Cancel(); };

// Configure Inputs
ParserResult<ActionInputs> parser = Parser.Default.ParseArguments<ActionInputs>(args);
if (parser.Errors.ToArray() is { Length: > 0 } errors)
{
    foreach (CommandLine.Error error in errors)
    { WriteLine($"{nameof(error)}: {error.Tag}"); }
    Environment.Exit(2);
    return;
}
ActionInputs inputs = parser.Value;

// Find Local Files
DirectoryInfo workspace = new(inputs.Workspace);
DirectoryInfo output = workspace.CreateSubdirectory(inputs.Output);

// Authentication credentials
Configuration config = new()
{
    Username = inputs.Username,
    Password = inputs.Password,
    UserAgent = "kemocade/0.0.1 admin%40kemocade.com"
};

// Create instances of APIs we'll need
AuthenticationApi authApi = new(config);
GroupsApi groupsApi = new(config);

// Build a mapping of User Display Names to RolePermissions
Dictionary<string, IEnumerable<RolePermission>> data;

try
{
    // Log in
    WriteLine("Logging in...");
    CurrentUser currentUser = authApi.GetCurrentUser();

    // Check if 2FA is needed
    if (currentUser == null)
    {
        WriteLine("2FA needed...");

        // Generate a 2FA code with the stored secret
        string key = inputs.Key.Replace(" ", string.Empty);
        Totp totp = new(Base32Encoding.ToBytes(key));

        // Make sure there's enough time left on the token
        int remainingSeconds = totp.RemainingSeconds();
        if (remainingSeconds < 5)
        {
            WriteLine("Waiting for new token...");
            await Task.Delay(TimeSpan.FromSeconds(remainingSeconds + 1));
        }

        // Verify 2FA
        WriteLine("Using 2FA code...");
        authApi.Verify2FA(new(totp.ComputeTotp()));
        currentUser = authApi.GetCurrentUser();
        if (currentUser == null)
        {
            WriteLine("Failed to validate 2FA!");
            Environment.Exit(2);
        }
    }
    WriteLine($"Logged in as {currentUser.DisplayName}");

    // Get all users from all tracked groups
    List<GroupMember> trackedUsers = new();
    List<GroupRole> trackedRoles = new();
    string[] groupIds = inputs.Groups.Split(',');
    foreach (string groupId in groupIds)
    {
        // Get group
        Group group = groupsApi.GetGroup(groupId);
        int memberCount = group.MemberCount;
        WriteLine($"Got Group {group.Name}, Members: {memberCount}");

        // Get self and ensure self is in group
        GroupMyMember self = group.MyMember;
        if (self == null)
        {
            WriteLine("User must be a member of the group!");
            Environment.Exit(2);
        }

        // Get group roles
        WriteLine("Getting Group Roles...");
        trackedRoles.AddRange(groupsApi.GetGroupRoles(groupId));

        // Get group members
        WriteLine("Getting Group Members...");
        List<GroupMember> groupMembers = new();

        // Get non-self group members and add to group members list
        while (groupMembers.Count < memberCount - 1)
        {
            groupMembers.AddRange
                (groupsApi.GetGroupMembers(groupId, 100, groupMembers.Count, 0));
            WriteLine(groupMembers.Count);
            await Task.Delay(1000);
        }

        // Get self group member and add to group members list
        WriteLine("Getting Self...");
        groupMembers.Add
        (
            new
            (
                self.Id,
                self.GroupId,
                self.UserId,
                self.IsRepresenting,
                new(currentUser.Id, currentUser.DisplayName),
                self.RoleIds,
                self.JoinedAt,
                self.MembershipStatus,
                self.Visibility,
                self.IsSubscribedToAnnouncements
            )
        );

        // Add to full tracked user list
        WriteLine($"Got {groupMembers.Count} Group Members");
        trackedUsers.AddRange(groupMembers);
    }

    data = trackedUsers
        .GroupBy(g => g.User.DisplayName)
        .OrderBy(g => g.Key)
        .ToDictionary
        (
            user => user.Key,
            user =>
            {
                IEnumerable<string> userRoleIds = user.SelectMany(g => g.RoleIds);

                // Merge all of this user's permissions across all groups & roles
                IEnumerable<RolePermission> perms = trackedRoles
                    .Where(r => userRoleIds.Contains(r.Id))
                    .SelectMany(r => r.Permissions)
                    .Select(r => FromString(r))
                    .Distinct()
                    .OrderBy(p => p);

                // If the user has the Owner permission, give all permissions
                return perms.Contains(Owner) ? Enum.GetValues<RolePermission>() : perms;
            }
        );
}
catch (ApiException e)
{
    WriteLine("Exception when calling API: {0}", e.Message);
    WriteLine("Status Code: {0}", e.ErrorCode);
    WriteLine(e.ToString());
    Environment.Exit(2);
    return;
}

// Build Json from data
string dataJsonString = Serialize
    (data, new JsonSerializerOptions(){ PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
WriteLine(dataJsonString);

// Write Json to file
FileInfo dataJsonFile = new(Path.Join(output.FullName, "data.json"));
WriteAllText(dataJsonFile.FullName, dataJsonString);

WriteLine("Done!");
Environment.Exit(0);

// Static Util
const string OWNER = "*";
const string MANAGE_GROUP_MEMBER_DATA = "group-members-manage";
const string MANAGE_GROUP_DATA = "group-data-manage";
const string VIEW_AUDIT_LOG = "group-audit-view";
const string MANAGE_GROUP_ROLES = "group-roles-manage";
const string ASSIGN_GROUP_ROLES = "group-roles-assign";
const string MANAGE_GROUP_BANS = "group-bans-manage";
const string REMOVE_GROUP_MEMBERS = "group-members-remove";
const string VIEW_ALL_MEMBERS = "group-members-viewall";
const string MANAGE_GROUP_ANNOUNCEMENT = "group-announcement-manage";
const string MANAGE_GROUP_GALLERIES = "group-galleries-manage";
const string MANAGE_GROUP_INVITES = "group-invites-manage";
const string MODERATE_GROUP_INSTANCES = "group-instance-moderate";
const string GROUP_INSTANCE_QUEUE_PRIORITY = "group-instance-queue-priority";
const string CREATE_GROUP_PUBLIC_INSTANCES = "group-instance-public-create";
const string CREATE_GROUP_PLUS_INSTANCES = "group-instance-plus-create";
const string CREATE_MEMBERS_ONLY_GROUP_INSTANCES = "group-instance-open-create";
const string ROLE_RESTRICT_MEMBERS_ONLY_INSTANCES = "group-instance-restricted-create";
const string PORTAL_TO_GROUP_PLUS_INSTANCES = "group-instance-plus-portal";
const string UNLOCKED_PORTAL_TO_GROUP_PLUS_INSTANCES = "group-instance-plus-portal-unlocked";
const string JOIN_GROUP_INSTANCES = "group-instance-join";

static RolePermission FromString(string name) =>
    name switch
    {
        OWNER => Owner,
        MANAGE_GROUP_MEMBER_DATA => ManageGroupMemberData,
        MANAGE_GROUP_DATA => ManageGroupData,
        VIEW_AUDIT_LOG => ViewAuditLog,
        MANAGE_GROUP_ROLES => ManageGroupRoles,
        ASSIGN_GROUP_ROLES => AssignGroupRoles,
        MANAGE_GROUP_BANS => ManageGroupBans,
        REMOVE_GROUP_MEMBERS => RemoveGroupMembers,
        VIEW_ALL_MEMBERS => ViewAllMembers,
        MANAGE_GROUP_ANNOUNCEMENT => ManageGroupAnnouncement,
        MANAGE_GROUP_GALLERIES => ManageGroupGalleries,
        MANAGE_GROUP_INVITES => ManageGroupInvites,
        MODERATE_GROUP_INSTANCES => ModerateGroupInstances,
        GROUP_INSTANCE_QUEUE_PRIORITY => GroupInstanceQueuePriority,
        CREATE_GROUP_PUBLIC_INSTANCES => CreateGroupPublicInstances,
        CREATE_GROUP_PLUS_INSTANCES => CreateGroupPlusInstances,
        CREATE_MEMBERS_ONLY_GROUP_INSTANCES => CreateMembersOnlyGroupInstances,
        ROLE_RESTRICT_MEMBERS_ONLY_INSTANCES => RoleRestrictMembersOnlyInstances,
        PORTAL_TO_GROUP_PLUS_INSTANCES => PortalToGroupPlusInstances,
        UNLOCKED_PORTAL_TO_GROUP_PLUS_INSTANCES => UnlockedPortalToGroupPlusInstances,
        JOIN_GROUP_INSTANCES => JoinGroupInstances,
        _ => throw new InvalidOperationException(),
    };