namespace NetRoute.Core;

/// Bytes the connection poll could not attribute to any connection, split by exit.
public readonly record struct UnseenSplit(long Phone, long Lan);

/// Turns "what sing-box says passed in total" and "what Windows says the phone adapter carried" into the bytes that
/// the per-connection numbers of one poll missed (connections that opened and closed between two polls).
///
/// Split is the stateless per-poll rule. The counter itself uses PhoneBalance, a signed running balance: the Windows
/// adapter counter is read a little after the HTTP snapshot and leads sing-box's counters by a few hundred ms, so at
/// the start of a burst the adapter is ahead of the connections (a positive difference) and at the end it is behind
/// (a negative one). Clamping each poll's difference at 0 would keep the first and drop the second, so the phone
/// column would drift above the adapter total. The balance carries the negative part forward instead, so the two cancel.
public static class UsageReconciler
{
    /// The signed phone difference of one poll: the adapter's bytes minus what the phone-exit connections explain,
    /// plus the deficit carried over from earlier polls (zero or negative). A result below zero is a deficit the
    /// caller takes back from the unattributed phone share and carries forward.
    public static long PhoneBalance(long carry, long phoneAdapterDelta, long seenPhoneDelta) =>
        carry + (phoneAdapterDelta - seenPhoneDelta);

    /// The unattributed LAN share: whatever the missed bytes are, minus the part booked to the phone this poll.
    public static long LanShare(long totalDelta, long seenDelta, long phoneBooked) =>
        Math.Max(0, Math.Max(0, totalDelta - seenDelta) - phoneBooked);


    public static UnseenSplit Split(long totalDelta, long seenDelta, long seenPhoneDelta, long? phoneAdapterDelta, bool phoneIsDefault)
    {
        var unseen = Math.Max(0, totalDelta - seenDelta);
        if (phoneAdapterDelta is not { } adapter)
        {
            var phone = phoneIsDefault ? unseen : 0;
            return new UnseenSplit(phone, unseen - phone);
        }
        // Protocol framing makes the adapter's count a few percent larger than sing-box's payload count. That difference
        // is real mobile data, so it stays in the phone share rather than being trimmed away.
        var unseenPhone = Math.Max(0, adapter - seenPhoneDelta);
        return new UnseenSplit(unseenPhone, Math.Max(0, unseen - unseenPhone));
    }
}
