using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;
using CourierEscrow.Domain.Ledger;

namespace CourierEscrow.Tests;

public class EscrowStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint Gps = new(-13.97, 33.78);

    private static Escrow NewEscrow(long item = 30_000_000, long delivery = 800_000, int bps = 200) =>
        Escrow.Create("NX-1042", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Samsung A15", item, delivery, bps, "MWK", null, null, "ops", Now);

    private static Escrow Funded()
    {
        var e = NewEscrow();
        e.ConfirmProduct("buyer", Now);
        e.StartPayment("ES1", "ops", Now);
        e.MarkFunded("system", Now);
        return e;
    }

    private static Escrow PickedUp()
    {
        var e = Funded();
        e.MarkPickedUp(new PickupEvidence(3, "SL-558201", 0.6m, Gps), "courier", Now);
        return e;
    }

    private static Escrow Delivered()
    {
        var e = PickedUp();
        e.MarkArrived(new ArrivalEvidence(1, Gps), "courier", Now);
        return e;
    }

    [Fact]
    public void The_worked_example_adds_up()
    {
        var e = NewEscrow();
        Assert.Equal(30_800_000, e.Total);
        Assert.Equal(600_000, e.PlatformFee);       // 2% of the ITEM only
        Assert.Equal(29_400_000, e.SellerProceeds);
        var s = Settlement.ForRelease(e);
        Assert.Equal(e.Total, s.Total);
        Assert.Equal(800_000, s.Courier);
    }

    [Fact]
    public void Bad_orders_are_rejected()
    {
        Assert.Throws<DomainValidationException>(() => NewEscrow(item: 0));
        Assert.Throws<DomainValidationException>(() => NewEscrow(delivery: -1));
        Assert.Throws<DomainValidationException>(() => NewEscrow(bps: 10_000));
        var same = Guid.NewGuid();
        Assert.Throws<DomainValidationException>(() => Escrow.Create("X", same, same, Guid.NewGuid(), "x", 100, 0, 0, "MWK", null, null, "ops", Now));
    }

    [Fact]
    public void The_happy_path_walks_every_state_in_order()
    {
        var e = NewEscrow();
        Assert.Equal(EscrowState.Created, e.State);
        e.ConfirmProduct("buyer", Now);
        e.StartPayment("ES1", "ops", Now); Assert.Equal(EscrowState.PaymentPending, e.State);
        e.MarkFunded("system", Now); Assert.Equal(EscrowState.Funded, e.State);
        e.MarkPickedUp(new PickupEvidence(3, "SL-1", 0.6m, Gps), "c", Now); Assert.Equal(EscrowState.PickedUp, e.State);
        Assert.Equal(600, e.WeightGrams);
        e.MarkArrived(new ArrivalEvidence(1, Gps), "c", Now); Assert.Equal(EscrowState.Delivered, e.State);
        e.Release("c", Now); Assert.Equal(EscrowState.Released, e.State);
        e.Settle("system", Now); Assert.Equal(EscrowState.Settled, e.State);
    }

    [Fact]
    public void No_state_can_be_skipped()
    {
        Assert.Throws<InvalidEscrowTransitionException>(() => NewEscrow().MarkFunded("s", Now));
        Assert.Throws<InvalidEscrowTransitionException>(() => Funded().Release("c", Now));
        Assert.Throws<InvalidEscrowTransitionException>(() => Funded().MarkArrived(new ArrivalEvidence(1, Gps), "c", Now));
        Assert.Throws<InvalidEscrowTransitionException>(() => PickedUp().Release("c", Now));
        Assert.Throws<InvalidEscrowTransitionException>(() => Delivered().Settle("s", Now));
    }

    [Fact]
    public void Pickup_needs_three_photos_a_seal_a_weight_and_gps()
    {
        Assert.Throws<DomainValidationException>(() => Funded().MarkPickedUp(new PickupEvidence(2, "SL", 0.6m, Gps), "c", Now));
        Assert.Throws<DomainValidationException>(() => Funded().MarkPickedUp(new PickupEvidence(3, " ", 0.6m, Gps), "c", Now));
        Assert.Throws<DomainValidationException>(() => Funded().MarkPickedUp(new PickupEvidence(3, "SL", 0, Gps), "c", Now));
        Assert.Throws<DomainValidationException>(() => Funded().MarkPickedUp(new PickupEvidence(3, "SL", 0.6m, null), "c", Now));
        Assert.Throws<DomainValidationException>(() => Funded().MarkPickedUp(new PickupEvidence(3, "SL", 0.6m, new GeoPoint(200, 0)), "c", Now));
    }

    [Fact]
    public void A_failed_guard_changes_nothing()
    {
        var e = Funded();
        var version = e.Version;
        Assert.Throws<DomainValidationException>(() => e.MarkPickedUp(new PickupEvidence(1, "SL", 0.6m, Gps), "c", Now));
        Assert.Equal(EscrowState.Funded, e.State);
        Assert.Equal(version, e.Version);
        Assert.Null(e.SealId);
    }

    [Fact]
    public void Every_change_writes_an_order_event_and_bumps_the_version()
    {
        var e = Delivered();
        var types = e.PendingOrderEvents.Select(x => x.Type).ToList();
        Assert.Equal("Created,ProductConfirmed,PaymentRequested,PaymentConfirmed,PickedUp,Arrived", string.Join(",", types));
        Assert.Equal(5, e.Version);
        Assert.Equal("Delivered", e.PendingOrderEvents[^1].ToState);
    }

    [Fact]
    public void Rejection_needs_a_reason_photos_and_gps_then_return_needs_seller_confirmation()
    {
        Assert.Throws<DomainValidationException>(() => Delivered().Reject(" ", 2, Gps, "c", Now));
        Assert.Throws<DomainValidationException>(() => Delivered().Reject("damaged", 1, Gps, "c", Now));

        var e = Delivered();
        e.Reject("damaged", 2, Gps, "c", Now);
        Assert.Equal(EscrowState.Rejected, e.State);
        Assert.Throws<DomainValidationException>(() => e.ConfirmReturn(1, false, "c", Now));
        e.ConfirmReturn(1, true, "c", Now);
        Assert.Equal(EscrowState.Returned, e.State);
    }

    [Fact]
    public void A_refund_after_return_gives_the_buyer_the_item_and_the_courier_the_delivery_fee()
    {
        var e = Delivered();
        e.Reject("wrong colour", 2, Gps, "c", Now);
        e.ConfirmReturn(1, true, "c", Now);

        var s = Settlement.ForRejection(e);
        Assert.Equal(30_000_000, s.BuyerRefund);
        Assert.Equal(800_000, s.Courier);
        Assert.Equal(0, s.Platform);
        e.Refund(s, "ops", Now);
        Assert.Equal(EscrowState.Refunded, e.State);
        e.Settle("system", Now);
    }

    [Fact]
    public void A_funded_order_can_be_refunded_in_full_before_pickup_but_not_after()
    {
        var e = Funded();
        e.Refund(Settlement.FullRefund(e), "ops", Now);
        Assert.Equal(EscrowState.Refunded, e.State);
        Assert.Throws<InvalidEscrowTransitionException>(() => { var p = PickedUp(); p.Refund(Settlement.FullRefund(p), "ops", Now); });
    }

    [Fact]
    public void A_refund_cannot_pay_the_seller_or_miscount_the_money()
    {
        var e = Funded();
        Assert.Throws<DomainValidationException>(() => e.Refund(new Settlement(0, 100, 0, e.Total - 100), "ops", Now));
        Assert.Throws<DomainValidationException>(() => e.Refund(new Settlement(e.Total - 1, 0, 0, 0), "ops", Now));
        Assert.Throws<DomainValidationException>(() => e.Refund(new Settlement(e.Total + 5, 0, 0, -5), "ops", Now));
    }

    [Fact]
    public void Cancel_works_only_before_the_order_is_funded()
    {
        var created = NewEscrow(); created.Cancel("changed mind", "ops", Now);
        Assert.Equal(EscrowState.Cancelled, created.State);
        Assert.NotNull(created.ClosedAt);

        var pending = NewEscrow(); pending.ConfirmProduct("buyer", Now); pending.StartPayment("ES1", "ops", Now); pending.Cancel("expired", "system", Now);
        Assert.Equal(EscrowState.Cancelled, pending.State);

        Assert.Throws<InvalidEscrowTransitionException>(() => Funded().Cancel("x", "ops", Now));
    }

    [Fact]
    public void Disputes_can_be_opened_from_any_live_state_but_never_after_release()
    {
        foreach (var make in new Func<Escrow>[] { Funded, PickedUp, Delivered })
        {
            var e = make();
            var from = e.State;
            e.OpenDispute("ops", Now);
            Assert.Equal(EscrowState.Disputed, e.State);
            Assert.Equal(from, e.DisputedFrom);
        }

        var released = Delivered(); released.Release("c", Now);
        Assert.Throws<InvalidEscrowTransitionException>(() => released.OpenDispute("ops", Now));
        Assert.Throws<InvalidEscrowTransitionException>(() => NewEscrow().OpenDispute("ops", Now));
    }

    [Fact]
    public void A_dispute_decision_releases_or_refunds_and_must_add_up()
    {
        var split = Delivered(); split.OpenDispute("ops", Now);
        split.ResolveDispute(new Settlement(10_000_000, 19_400_000, 800_000, 600_000), "reviewer", Now);
        Assert.Equal(EscrowState.Released, split.State);

        var refund = Delivered(); refund.OpenDispute("ops", Now);
        refund.ResolveDispute(Settlement.ForRejection(refund), "reviewer", Now);
        Assert.Equal(EscrowState.Refunded, refund.State);

        var bad = Delivered(); bad.OpenDispute("ops", Now);
        Assert.Throws<DomainValidationException>(() => bad.ResolveDispute(new Settlement(1, 1, 1, 1), "reviewer", Now));
    }

    [Fact]
    public void Domain_events_are_queued_for_the_outbox()
    {
        var e = Delivered();
        Assert.Equal("EscrowCreated,EscrowProductConfirmed,EscrowPaymentRequested,EscrowFunded,EscrowPickedUp,EscrowDelivered", string.Join(",", e.PendingEvents.Select(x => x.Type)));
        e.ClearEvents();
        Assert.Empty(e.PendingEvents);
        Assert.Empty(e.PendingOrderEvents);
    }
}

public class LedgerPostingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static Escrow Order() => Escrow.Create("NX-1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Phone", 30_000_000, 800_000, 200, "MWK", null, null, "ops", Now);

    private static (long Debits, long Credits) Totals(LedgerTransaction t) =>
        (t.Entries.Where(e => e.Direction == Direction.Debit).Sum(e => e.Amount), t.Entries.Where(e => e.Direction == Direction.Credit).Sum(e => e.Amount));

    [Fact]
    public void An_unbalanced_or_empty_transaction_cannot_be_created()
    {
        Assert.Throws<DomainValidationException>(() => LedgerTransaction.Create(Guid.NewGuid(), "x", "k", Now,
            [new LedgerEntry("a", Direction.Debit, 100, "MWK"), new LedgerEntry("b", Direction.Credit, 99, "MWK")]));
        Assert.Throws<DomainValidationException>(() => LedgerTransaction.Create(Guid.NewGuid(), "x", "k", Now,
            [new LedgerEntry("a", Direction.Debit, 100, "MWK")]));
        Assert.Throws<DomainValidationException>(() => LedgerTransaction.Create(Guid.NewGuid(), "x", "k", Now,
            [new LedgerEntry("a", Direction.Debit, 0, "MWK"), new LedgerEntry("b", Direction.Credit, 0, "MWK")]));
    }

    [Fact]
    public void Funding_matches_the_design_example()
    {
        var e = Order();
        var t = EscrowPostings.Funding(e, "funding:1", Now);
        Assert.Equal(30_800_000, t.Entries.Single(x => x.Account == Accounts.ProviderClearing && x.Direction == Direction.Debit).Amount);
        Assert.Equal(30_800_000, t.Entries.Single(x => x.Account == Accounts.Escrow(e.Id) && x.Direction == Direction.Credit).Amount);
    }

    [Fact]
    public void Release_splits_seller_fee_and_courier_exactly_like_the_design()
    {
        var e = Order();
        var t = EscrowPostings.Settle(e, Settlement.ForRelease(e), "release", Now);
        Assert.Equal(Totals(t).Debits, Totals(t).Credits);
        Assert.Equal(29_400_000, t.Entries.Single(x => x.Account == Accounts.SellerPayable(e.SellerId)).Amount);
        Assert.Equal(600_000, t.Entries.Single(x => x.Account == Accounts.PlatformRevenue).Amount);
        Assert.Equal(800_000, t.Entries.Single(x => x.Account == Accounts.CourierPayable(e.CourierId)).Amount);
        Assert.Equal(30_800_000, t.Entries.Single(x => x.Account == Accounts.Escrow(e.Id) && x.Direction == Direction.Debit).Amount);
    }

    [Fact]
    public void A_rejection_refund_credits_the_buyer_and_the_courier_and_no_platform_fee()
    {
        var e = Order();
        var t = EscrowPostings.Settle(e, Settlement.ForRejection(e), "refund", Now);
        Assert.Equal(Totals(t).Debits, Totals(t).Credits);
        Assert.Equal(30_000_000, t.Entries.Single(x => x.Account == Accounts.BuyerRefundable(e.BuyerId)).Amount);
        Assert.False(t.Entries.Any(x => x.Account == Accounts.PlatformRevenue));
    }

    [Fact]
    public void Settlement_idempotency_key_is_per_order_so_it_cannot_be_posted_twice()
    {
        var e = Order();
        Assert.Equal(EscrowPostings.Settle(e, Settlement.ForRelease(e), "release", Now).IdempotencyKey,
                     EscrowPostings.Settle(e, Settlement.ForRelease(e), "release", Now).IdempotencyKey);
    }

    [Fact]
    public void A_confirmed_payout_clears_the_payable_and_books_the_provider_fee()
    {
        var e = Order();
        var p = Payout.Create(e, PayoutKind.Seller, e.SellerId, "Chikondi", "265991234567", MobileNetwork.Airtel.ToString().ToString(), 29_400_000, Now);
        var t = EscrowPostings.PayoutConfirmed(p, 100_000, Now);
        Assert.Equal(Totals(t).Debits, Totals(t).Credits);
        Assert.Equal(29_500_000, t.Entries.Single(x => x.Account == Accounts.ProviderClearing).Amount);
        Assert.Equal(100_000, t.Entries.Single(x => x.Account == Accounts.ProviderFees).Amount);
    }
}

public class OtpAndPayoutDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_code_is_never_stored_and_the_hash_is_keyed_per_order()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var otp = Otp.Issue(a, "secret", "481902", Now.AddHours(24));
        Assert.False(otp.Hmac.Contains("481902"));
        Assert.NotEqual(OtpHasher.Compute("secret", a, "481902"), OtpHasher.Compute("secret", b, "481902"));
        Assert.NotEqual(OtpHasher.Compute("secret", a, "481902"), OtpHasher.Compute("other", a, "481902"));
    }

    [Fact]
    public void Codes_are_six_digits()
    {
        for (var i = 0; i < 200; i++) Assert.Equal(6, OtpHasher.NewCode().Length);
    }

    [Fact]
    public void The_right_code_works_once_with_spaces_ignored()
    {
        var id = Guid.NewGuid();
        var otp = Otp.Issue(id, "s", "481902", Now.AddHours(24));
        Assert.Equal(OtpResult.Accepted, otp.Verify("s", " 481902 ", Now).Result);
        Assert.Equal(OtpResult.NotActive, otp.Verify("s", "481902", Now).Result);
    }

    [Fact]
    public void Five_wrong_attempts_lock_the_code_even_for_the_right_one()
    {
        var otp = Otp.Issue(Guid.NewGuid(), "s", "481902", Now.AddHours(24));
        for (var i = 1; i <= 4; i++)
        {
            var r = otp.Verify("s", "000000", Now);
            Assert.Equal(OtpResult.Wrong, r.Result);
            Assert.Equal(5 - i, r.AttemptsLeft);
        }
        Assert.Equal(OtpResult.Locked, otp.Verify("s", "000000", Now).Result);
        Assert.Equal(OtpResult.Locked, otp.Verify("s", "481902", Now).Result);
        Assert.Equal(OtpStatus.Locked, otp.Status);
    }

    [Fact]
    public void An_expired_code_is_refused_without_using_an_attempt_and_can_be_reissued()
    {
        var id = Guid.NewGuid();
        var otp = Otp.Issue(id, "s", "481902", Now.AddHours(24));
        Assert.Equal(OtpResult.Expired, otp.Verify("s", "481902", Now.AddHours(25)).Result);
        Assert.Equal(0, otp.Attempts);

        otp.Reissue("s", "111222", Now.AddHours(48));
        Assert.Equal(OtpResult.Accepted, otp.Verify("s", "111222", Now.AddHours(26)).Result);
    }

    private static Payout NewPayout()
    {
        var e = Escrow.Create("NX-1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "x", 1_000_000, 0, 0, "MWK", null, null, "ops", Now);
        return Payout.Create(e, PayoutKind.Seller, e.SellerId, "Seller", "265991234567", MobileNetwork.Airtel.ToString(), 1_000_000, Now);
    }

    [Fact]
    public void A_payout_reference_is_16_characters_and_stable_for_the_same_order_and_kind()
    {
        var e = Escrow.Create("NX-1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "x", 1_000_000, 0, 0, "MWK", null, null, "ops", Now);
        var a = Payout.Create(e, PayoutKind.Seller, e.SellerId, "S", "265991234567", MobileNetwork.Airtel.ToString(), 100, Now);
        var b = Payout.Create(e, PayoutKind.Seller, e.SellerId, "S", "265991234567", MobileNetwork.Airtel.ToString(), 100, Now);
        var c = Payout.Create(e, PayoutKind.Courier, e.CourierId, "C", "265991234567", MobileNetwork.Airtel.ToString(), 100, Now);
        Assert.Equal(16, a.Reference.Length);
        Assert.Equal(a.Reference, b.Reference);
        Assert.NotEqual(a.Reference, c.Reference);
    }

    [Fact]
    public void Transient_failures_back_off_keep_the_same_key_and_end_in_review()
    {
        var p = NewPayout();
        var key = p.Reference;
        p.RecordTransientFailure("timeout", Now);
        Assert.Equal(PayoutStatus.Queued, p.Status);
        Assert.Equal(Now.AddMinutes(1), p.NextAttemptAt);
        Assert.Equal(key, p.Reference);
        for (var i = 0; i < Payout.MaxAttempts; i++) p.RecordTransientFailure("timeout", Now);
        Assert.Equal(PayoutStatus.NeedsReview, p.Status);
        Assert.Equal(key, p.Reference);
    }

    [Fact]
    public void A_failure_reported_by_the_provider_retries_under_a_new_key()
    {
        var p = NewPayout();
        var key = p.Reference;
        p.MarkSubmitted("TX1", Now);
        p.FailedAtProvider("wallet closed", Now);
        Assert.Equal(PayoutStatus.Queued, p.Status);
        Assert.NotEqual(key, p.Reference);
        Assert.Null(p.ProviderRef);
    }

    [Fact]
    public void A_payout_without_a_wallet_network_goes_straight_to_review()
    {
        var e = Escrow.Create("NX-1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "x", 1_000_000, 0, 0, "MWK", null, null, "ops", Now);
        Assert.Equal(PayoutStatus.NeedsReview, Payout.Create(e, PayoutKind.Seller, e.SellerId, "S", "265991234567", null, 100, Now).Status);
    }

    [Fact]
    public void Retry_is_only_for_payouts_that_need_review()
    {
        var p = NewPayout();
        Assert.Throws<DomainValidationException>(() => p.Retry(false, Now));
        p.NeedsAttention("bad account");
        var key = p.Reference;
        p.Retry(newKey: true, Now);
        Assert.Equal(PayoutStatus.Queued, p.Status);
        Assert.NotEqual(key, p.Reference);
    }

    [Fact]
    public void Phone_numbers_are_normalised_and_masked()
    {
        Assert.Equal("265991234567", PhoneNumber.Normalize("0991 234 567"));
        Assert.Equal("265991234567", PhoneNumber.Normalize("+265 99 123 4567"));
        Assert.Equal("265991234567", PhoneNumber.Normalize("991234567"));
        Assert.Throws<DomainValidationException>(() => PhoneNumber.Normalize("12345"));
        Assert.Throws<DomainValidationException>(() => PhoneNumber.Normalize("+254712345678"));
        Assert.Equal("****4567", PhoneNumber.Mask("265991234567"));
    }
}
