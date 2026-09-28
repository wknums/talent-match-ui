namespace TalentMatch.Domain.Entities;

public class ProcessingEvent
{
    public static class AuthorizationActions
    {
        public const string LoginSucceeded = "auth.login.succeeded";
        public const string LoginDenied = "auth.login.denied";
        public const string ProfilePending = "auth.profile.pending";
        public const string TokenStale = "auth.token.stale";
        public const string Logout = "auth.logout";
        public const string AssignmentActivated = "auth.assignment.activated";
        public const string AssignmentRevoked = "auth.assignment.revoked";
        public const string MembershipActivated = "auth.membership.activated";
        public const string MembershipRevoked = "auth.membership.revoked";
        public const string GroupMappingUpdated = "auth.group_mapping.updated";
        public const string ScopeDenied = "auth.scope.denied";
        public const string NavigationShellChanged = "auth.navigation.changed";
    }

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Actor { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
}
