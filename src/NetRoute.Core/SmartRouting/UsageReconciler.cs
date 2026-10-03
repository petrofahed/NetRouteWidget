namespace NetRoute.Core;

/// Bytes the connection poll could not attribute to any connection, split by exit.
public readonly record struct UnseenSplit(long Phone, long Lan);

/// Turns "what sing-box says passed in total" and "what Windows says the phone adapter carried" into the bytes that
/// the per-connection numbers of one poll missed (connections that opened and closed between two polls).
public static class UsageReconciler
{
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
