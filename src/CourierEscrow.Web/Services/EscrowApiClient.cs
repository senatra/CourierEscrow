using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using CourierEscrow.Web.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CourierEscrow.Web.Services;

public sealed class ApiException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The console is a thin client of the escrow API. Every call goes out with the signed-in person's own access token
/// (kept in their encrypted session cookie), so the API knows who did what and enforces their role.
/// </summary>
public sealed class EscrowApiClient(HttpClient http, AuthenticationStateProvider auth, NavigationManager nav)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<HttpRequestMessage> Build(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, options: Json) };
        var token = (await auth.GetAuthenticationStateAsync()).User.FindFirst(ConsoleSession.TokenClaim)?.Value;
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new("Bearer", token);
        return req;
    }

    private async Task<T> Send<T>(HttpMethod method, string url, object? body = null)
    {
        using var req = await Build(method, url, body);
        using var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) throw await Failure(res, req.Headers.Authorization is not null);
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async Task<ApiException> Failure(HttpResponseMessage res, bool signedIn)
    {
        // The session was revoked or expired (user disabled, password changed elsewhere, token lifetime reached): back to sign-in.
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized && signedIn)
        {
            nav.NavigateTo("/account/expired", forceLoad: true);
            return new ApiException("Your session has ended. Sign in again.", 401);
        }

        var text = await res.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
            return new ApiException(detail ?? title ?? $"Request failed ({(int)res.StatusCode}).", (int)res.StatusCode);
        }
        catch (JsonException)
        {
            return new ApiException(res.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Your session has ended. Sign in again.",
                System.Net.HttpStatusCode.Forbidden => "Your role is not allowed to do this.",
                System.Net.HttpStatusCode.TooManyRequests => "Too many attempts. Wait a minute and try again.",
                _ => $"Request failed ({(int)res.StatusCode})."
            }, (int)res.StatusCode);
        }
    }

    private Task<T> Get<T>(string url) => Send<T>(HttpMethod.Get, url);
    private Task<T> Post<T>(string url, object? body = null) => Send<T>(HttpMethod.Post, url, body ?? new { });

    // Sign-in and your own account
    public Task<LoginResponse> Login(string username, string password) => Post<LoginResponse>("/auth/login", new { username, password });
    public Task<LoginResponse> ChangePassword(string currentPassword, string newPassword) => Post<LoginResponse>("/auth/change-password", new { currentPassword, newPassword });

    // Staff users (administrators)
    public Task<List<StaffUserRow>> GetUsers() => Get<List<StaffUserRow>>("/admin/users");
    public Task<StaffUserRow> CreateUser(string username, string displayName, string role, string password) => Post<StaffUserRow>("/admin/users", new { username, displayName, role, password });
    public Task<StaffUserRow> SetUserRole(Guid id, string role) => Post<StaffUserRow>($"/admin/users/{id}/role", new { role });
    public Task<StaffUserRow> SetUserActive(Guid id, bool active) => Post<StaffUserRow>($"/admin/users/{id}/status", new { active });
    public Task<StaffUserRow> ResetUserPassword(Guid id, string newPassword) => Post<StaffUserRow>($"/admin/users/{id}/reset-password", new { newPassword });
    public Task<StaffUserRow> UnlockUser(Guid id) => Post<StaffUserRow>($"/admin/users/{id}/unlock");

    public Task<Summary> GetSummary() => Get<Summary>("/admin/summary");
    public Task<List<OrderSummary>> GetOrders(string? state = null) => Get<List<OrderSummary>>("/admin/orders?take=100" + (string.IsNullOrEmpty(state) ? "" : $"&state={state}"));
    public Task<List<OrderSummary>> GetFlagged() => Get<List<OrderSummary>>("/admin/orders/flagged");
    public Task<OrderView> GetOrder(Guid id) => Get<OrderView>($"/admin/orders/{id}");

    public Task<OrderSummary> CreateOrder(NewOrderForm f, long item, long delivery, IReadOnlyList<Guid> listingUploadIds) => Post<OrderSummary>("/admin/orders", new
    {
        f.BuyerName,
        f.BuyerPhone,
        buyerNetwork = f.BuyerNetwork,
        f.SellerName,
        f.SellerPhone,
        sellerNetwork = f.SellerNetwork,
        f.CourierId,
        f.ItemDescription,
        itemAmount = item,
        deliveryFee = delivery,
        f.PickupAddress,
        f.DeliveryAddress,
        listingUploadIds
    });

    public Task<PaymentInfo> RequestPayment(Guid id) => Post<PaymentInfo>($"/admin/orders/{id}/payment");
    public Task Cancel(Guid id, string reason) => Post<JsonElement>($"/admin/orders/{id}/cancel", new { reason });
    public Task Refund(Guid id, string reason) => Post<JsonElement>($"/admin/orders/{id}/refund", new { reason });
    public Task ReissueOtp(Guid id) => Post<JsonElement>($"/admin/orders/{id}/otp/reissue");
    public Task ClearFlag(Guid id) => Post<JsonElement>($"/admin/orders/{id}/clear-flag");
    public Task OpenDispute(Guid id, string reason) => Post<DisputeRow>($"/admin/orders/{id}/dispute", new { reason });

    public async Task<string> GetProofImage(Guid orderId, Guid proofId)
    {
        using var req = await Build(HttpMethod.Get, $"/admin/orders/{orderId}/proofs/{proofId}/file");
        using var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) throw await Failure(res, req.Headers.Authorization is not null);
        return "data:image/jpeg;base64," + Convert.ToBase64String(await res.Content.ReadAsByteArrayAsync());
    }

    public Task<List<DisputeRow>> GetDisputes(string? status = null) => Get<List<DisputeRow>>("/admin/disputes" + (status is null ? "" : $"?status={status}"));
    public Task<DisputeRow> DecideDispute(Guid id, string outcome, string reason, long buyer = 0, long seller = 0, long courier = 0, long platform = 0) =>
        Post<DisputeRow>($"/admin/disputes/{id}/decide", new { outcome, reason, buyerAmount = buyer, sellerAmount = seller, courierAmount = courier, platformAmount = platform });

    public Task<List<PayoutRow>> GetPayouts(string? status = null) => Get<List<PayoutRow>>("/admin/payouts" + (string.IsNullOrEmpty(status) ? "" : $"?status={status}"));
    public Task<PayoutRow> RetryPayout(Guid id, bool newKey, string? msisdn = null, string? network = null) => Post<PayoutRow>($"/admin/payouts/{id}/retry", new { newKey, msisdn, network });

    public Task<List<CourierRow>> GetCouriers() => Get<List<CourierRow>>("/admin/couriers");
    public Task<CourierRow> CreateCourier(string name, string phone, string network, string pin) => Post<CourierRow>("/admin/couriers", new { name, phone, network, pin });
    public Task<CourierRow> ResetDevice(Guid id) => Post<CourierRow>($"/admin/couriers/{id}/reset-device");
    public Task<CourierRow> SetCourierActive(Guid id, bool active) => Post<CourierRow>($"/admin/couriers/{id}/status", new { active });

    public Task<List<ReconRun>> GetReconRuns() => Get<List<ReconRun>>("/admin/reconciliation");
    public Task<List<ReconItem>> GetReconItems(Guid runId) => Get<List<ReconItem>>($"/admin/reconciliation/{runId}");
    public Task<JsonElement> RunReconciliation(DateOnly date) => Post<JsonElement>($"/admin/reconciliation/run?date={date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
    public Task<PaymentProviderSelection> GetPaymentProvider() => Get<PaymentProviderSelection>("/admin/payment-provider");
    public Task<PaymentProviderSelection> SetPaymentProvider(string provider) => Post<PaymentProviderSelection>("/admin/payment-provider", new { provider });
    public Task<JsonElement> TopUpOneKhusaSandbox(decimal amount) => Post<JsonElement>("/admin/payment-provider/sandbox-topup", new { amount });
    public Task<JsonElement> SimulateOneKhusaPayment(Guid orderId) => Post<JsonElement>($"/admin/orders/{orderId}/simulate-onekhusa-payment");

    /// <summary>Development only: plays the provider's "payment succeeded" webhook against the mock provider.</summary>
    public Task<JsonElement> SimulateBuyerApproval(Guid orderId) => Post<JsonElement>($"/dev/approve/{orderId}");
    public Task<List<DevSmsRow>> DevSms(Guid orderId) => Get<List<DevSmsRow>>($"/dev/sms?escrowId={orderId}");
    public Task<JsonElement> SimulatePayment(Guid orderId) => Post<JsonElement>($"/dev/pay/{orderId}");
    public Task<JsonElement> SimulatePickup(string token) => Post<JsonElement>($"/dev/pickup/{token}");
    public Task<JsonElement> SimulateArrival(string token) => Post<JsonElement>($"/dev/deliver/{token}");
    public Task<JsonElement> SimulateAccept(string token) => Post<JsonElement>($"/dev/accept/{token}");
    public Task<JsonElement> SimulatePayoutSuccess(string reference, long amount) => Post<JsonElement>($"/dev/payout-success/{reference}?amount={amount}");

    private sealed record UploadSlot(Guid UploadId, string Url, DateTimeOffset ExpiresAt);

    /// <summary>Ops listing photos: get a signed slot, PUT the bytes, return the upload ids for CreateOrder.</summary>
    public async Task<List<Guid>> UploadListingPhotos(IEnumerable<byte[]> photos)
    {
        var ids = new List<Guid>();
        foreach (var bytes in photos)
        {
            var slot = await Post<UploadSlot>("/admin/uploads/presign", new { contentType = "image/jpeg" });
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new("image/jpeg");
            var uploadPath = new Uri(slot.Url, UriKind.Absolute).PathAndQuery;
            using var res = await http.PutAsync(uploadPath, content);
            if (!res.IsSuccessStatusCode) throw await Failure(res, false);
            ids.Add(slot.UploadId);
        }
        return ids;
    }

}
