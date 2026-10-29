namespace CourierEscrow.Api;

public sealed record StaffLoginRequest(string Username, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record CreateStaffUserRequest(string Username, string DisplayName, string Role, string Password);
public sealed record StaffProfileRequest(string DisplayName);
public sealed record StaffRoleRequest(string Role);
public sealed record StaffStatusRequest(bool Active);
public sealed record ResetPasswordRequest(string NewPassword);
