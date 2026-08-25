namespace FrameSyncDemo
{
    public enum RouteCProtocolDropReason : byte
    {
        None = 0,
        HeaderTooShort = 1,
        DatagramTooLarge = 2,
        DatagramLengthOutOfRange = 3,
        InvalidMagic = 4,
        UnsupportedVersion = 5,
        UnknownMessageType = 6,
        PayloadTooLarge = 7,
        PayloadLengthMismatch = 8,
        SessionNotFound = 9,
        StaleGeneration = 10,
        WrongEndpoint = 11,
        KcpPayloadTooShort = 12,
        WrongConversation = 13,
        RawDatagramTooLarge = 14,
        InvalidRawControlPayload = 15,
        InvalidRawInputPayload = 16,
        InvalidRawWindowSize = 17,
        WrongPlayer = 18,
        WrongMessageDirection = 19,
        PacketTooOld = 20,
        InputTooOld = 21,
        PacketSequenceConflict = 22,
        PacketSequenceAmbiguous = 23
    }
}
