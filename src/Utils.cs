using NdefLibrary.Ndef;
using PCSC;
using PCSC.Iso7816;
using PCSC.Utils;
using System.Text;
using System.Numerics;

namespace IO.NFC;

public static class Utils
{
    // ============================================================
    //  Hilfsfunktionen
    // ============================================================

    /// <summary>
    /// Prüft, ob die meisten Zeichen im String druckbar sind.
    /// </summary>
    private static bool IsMostlyPrintable(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;

        int printable = 0;
        foreach (char c in s)
        {
            if (c == '\r' || c == '\n' || c == '\t' ||
                (c >= 32 && c < 127))
            {
                printable++;
            }
        }

        double ratio = (double)printable / s.Length;
        return ratio >= 0.8;
    }

    /// <summary>
    /// Listet alle verfügbaren PC/SC-Reader (z.B. ACR1552U).
    /// </summary>
    public static string[] ListAllReaders()
    {
        var contextFactory = ContextFactory.Instance;
        using var context = contextFactory.Establish(SCardScope.System);

        var readers = context.GetReaders();
        if (readers == null || readers.Length == 0)
        {
            Console.WriteLine("No NFC readers found. Please check driver installation.");
            return Array.Empty<string>();
        }

        Console.WriteLine("Available NFC Readers:");
        foreach (var r in readers)
            Console.WriteLine(" - " + r);

        return readers;
    }

    /// <summary>
    /// Gibt den ersten Readernamen zurück.
    /// </summary>
    public static string GetFirstReader(ISCardContext context)
    {
        var readers = context.GetReaders();
        if (readers == null || readers.Length == 0)
            throw new Exception("No smart card readers found. Is your NFC reader driver installed?");

        return readers[0];
    }

    /// <summary>
    /// APDU senden (PC/SC), inkl. ACS-spezifischer Kommandos (CLA=FF).
    /// </summary>
    public static ResponseApdu TransmitApdu(SCardReader reader, CommandApdu apdu)
    {
        byte[] sendBuffer = apdu.ToArray();

        IntPtr sendPci = SCardPCI.GetPci(reader.ActiveProtocol);
        var receivePci = new SCardPCI();

        byte[] receiveBuffer = new byte[258];
        int receiveLength = receiveBuffer.Length;

        var rc = reader.Transmit(
            sendPci,
            sendBuffer,
            sendBuffer.Length,
            receivePci,
            receiveBuffer,
            ref receiveLength);

        if (rc != SCardError.Success)
            throw new Exception("APDU transmit error: " + SCardHelper.StringifyError(rc));

        return new ResponseApdu(
            receiveBuffer,
            receiveLength,
            apdu.Case,
            reader.ActiveProtocol);
    }

    /// <summary>
    /// Liest die UID (FF CA 00 00 00). Funktioniert auch bei ISO15693 über ACR1552U.
    /// </summary>
    public static string GetCardUid(SCardReader reader)
    {
        var apdu = new CommandApdu(IsoCase.Case2Short, reader.ActiveProtocol)
        {
            CLA = 0xFF,
            INS = 0xCA,
            P1 = 0x00,
            P2 = 0x00,
            Le = 0x00
        };

        var response = TransmitApdu(reader, apdu);

        if (response.SW1 == 0x90 && response.SW2 == 0x00)
        {
            var data = response.GetData();
            if (data != null && data.Length > 0)
            {
                return BitConverter.ToString(data).Replace("-", "").ToUpperInvariant();
            }
        }

        Console.WriteLine("Failed to read UID, SW1={0:X2}, SW2={1:X2}", response.SW1, response.SW2);
        return string.Empty;
    }

    /// <summary>
    /// Nimmt Hexstring (z.B. "04A1CCB1320289"), dreht Byte-Reihenfolge und konvertiert in Dezimal.
    /// </summary>
    public static void ReverseHexToDecimal(
        string hexInput,
        out string reversedHex,
        out long decimalValue)
    {
        reversedHex = string.Empty;
        decimalValue = 0;

        if (string.IsNullOrEmpty(hexInput))
            return;

        hexInput = hexInput.Replace(" ", "").ToUpperInvariant();

        if (hexInput.Length % 2 != 0)
            throw new ArgumentException("Hex string must have an even number of characters.", nameof(hexInput));

        int byteCount = hexInput.Length / 2;
        byte[] bytes = new byte[byteCount];
        for (int i = 0; i < byteCount; i++)
            bytes[i] = Convert.ToByte(hexInput.Substring(i * 2, 2), 16);

        Array.Reverse(bytes);
        reversedHex = BitConverter.ToString(bytes).Replace("-", "");
        decimalValue = Convert.ToInt64(reversedHex, 16);
    }

    /// <summary>
    /// Baut eine URL mit ?uid=... oder &amp;uid=..., je nach vorhandenen Parametern.
    /// </summary>
    public static string BuildUrlWithUid(string baseUrl, string uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            Console.WriteLine("No UID available, writing base URL without uid parameter.");
            return baseUrl;
        }

        string sep = baseUrl.Contains('?') ? "&" : "?";
        return baseUrl + sep + "uid=" + uid;
    }

    // ============================================================
    //  ISO15693 / Type 5 (ACR1552U / WalletMate II)
    // ============================================================

    /// <summary>
    /// Liest mehrere ISO15693-Blöcke mit ACS-spezifischem "Read Multiple Blocks":
    ///   CLA=FF, INS=FB, P1=00, P2=00, Data = 23, FirstBlock, NumBlocksMinus1
    /// Antwort: [BlockData...] 90 00
    /// </summary>
    public static bool ReadIso15693MultipleBlocks(
        SCardReader reader,
        byte firstBlock,
        byte numberOfBlocksMinus1,
        out byte[] data,
        out string status)
    {
        data = Array.Empty<byte>();
        status = string.Empty;

        try
        {
            var apdu = new CommandApdu(IsoCase.Case3Short, reader.ActiveProtocol)
            {
                CLA = 0xFF,
                INS = 0xFB,
                P1 = 0x00,
                P2 = 0x00,
                Data = new byte[]
                {
                    0x23,              // Read Multiple Blocks
                    firstBlock,        // First block
                    numberOfBlocksMinus1
                }
            };

            var response = TransmitApdu(reader, apdu);

            if (response.SW1 == 0x90 && response.SW2 == 0x00)
            {
                var d = response.GetData();
                if (d == null || d.Length == 0)
                {
                    status = "No data returned from ISO15693 Read Multiple Blocks.";
                    return false;
                }

                data = d;
                status = "OK";
                return true;
            }
            else
            {
                status = $"ISO15693 ReadMultipleBlocks failed, SW1={response.SW1:X2}, SW2={response.SW2:X2}";
                return false;
            }
        }
        catch (Exception ex)
        {
            status = "Exception during ISO15693 ReadMultipleBlocks: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Schreibt einen ISO15693-Block mittels "Write Single Block":
    ///   CLA=FF, INS=FB, P1=00, P2=00, Data = 21, BlockNumber, [BlockData...]
    /// BlockData muss genau blockSize Bytes lang sein (bei SLIX2 in der Regel 4).
    /// </summary>
    public static bool WriteIso15693SingleBlock(
        SCardReader reader,
        byte blockNumber,
        byte[] blockData,
        out string status)
    {
        status = string.Empty;

        if (blockData == null || blockData.Length == 0)
        {
            status = "BlockData is null or empty.";
            return false;
        }

        var data = new byte[2 + blockData.Length];
        data[0] = 0x21;        // Write Single Block
        data[1] = blockNumber;
        Array.Copy(blockData, 0, data, 2, blockData.Length);

        var apdu = new CommandApdu(IsoCase.Case3Short, reader.ActiveProtocol)
        {
            CLA = 0xFF,
            INS = 0xFB,
            P1 = 0x00,
            P2 = 0x00,
            Data = data
        };

        try
        {
            var response = TransmitApdu(reader, apdu);

            if (response.SW1 == 0x90 && response.SW2 == 0x00)
            {
                status = "OK";
                return true;
            }

            status = $"ISO15693 WriteSingleBlock failed, SW1={response.SW1:X2}, SW2={response.SW2:X2}";
            return false;
        }
        catch (Exception ex)
        {
            status = "Exception during ISO15693 WriteSingleBlock: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Debug: schreibt einen Testblock und liest ihn wieder zurück.
    /// So hast du die Sicherheit, dass Write/Read funktionieren.
    /// </summary>
    public static bool TestWriteReadBlockType5(
        SCardReader reader,
        string readerName,
        byte blockNumber,
        out byte[] readBack,
        out string status)
    {
        readBack = Array.Empty<byte>();
        status = string.Empty;

        if (reader == null || reader.ActiveProtocol == SCardProtocol.Unset)
        {
            status = $"Reader '{readerName}' is not connected.";
            return false;
        }

        try
        {
            byte[] testData = { 0x11, 0x22, 0x33, 0x44 };

            if (!WriteIso15693SingleBlock(reader, blockNumber, testData, out string writeStatus))
            {
                status = $"Write block {blockNumber} failed: {writeStatus}";
                return false;
            }

            if (!ReadIso15693MultipleBlocks(reader, blockNumber, 0x00, out var data, out string readStatus))
            {
                status = $"Read block {blockNumber} failed: {readStatus}";
                return false;
            }

            readBack = data;
            status = "OK";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Exception in TestWriteReadBlockType5: {ex.Message}";
            return false;
        }
    }

    // ============================================================
    //  NDEF / TLV für Type 5 (ISO15693)
    // ============================================================

    /// <summary>
    /// Sucht im gesamten Memory-Buffer nach einem gültigen NDEF-TLV (Type 0x03)
    /// und extrahiert die NDEF-Payload.
    /// - überspringt optional Type-5-CC (E1 40 .. ..)
    /// - unterstützt 1-Byte- und 3-Byte-Längen.
    /// </summary>
    private static bool TryExtractNdefFromTlv(
        byte[] buffer,
        out byte[] ndefPayload,
        out string error)
    {
        ndefPayload = Array.Empty<byte>();
        error = string.Empty;

        if (buffer == null || buffer.Length < 3)
        {
            error = "Buffer too small for TLV.";
            return false;
        }

        int i = 0;

        // Optional: Capability Container (CC) erkennen (E1 40 .. ..)
        if (buffer[0] == 0xE1 && buffer.Length >= 4)
        {
            // Wir springen einfach über die 4 CC-Bytes hinweg.
            i = 4;
        }

        while (i < buffer.Length)
        {
            byte t = buffer[i];

            if (t == 0x00)
            {
                // NULL TLV -> 1 Byte
                i += 1;
                continue;
            }

            if (t == 0xFE)
            {
                // Terminator TLV
                break;
            }

            if (i + 1 >= buffer.Length)
            {
                error = "Incomplete TLV length field.";
                return false;
            }

            if (t != 0x03)
            {
                // Fremder TLV -> Länge auslesen und überspringen
                byte lenByte = buffer[i + 1];
                int len;
                if (lenByte != 0xFF)
                {
                    len = lenByte;
                    i += 2 + len;
                }
                else
                {
                    if (i + 3 >= buffer.Length)
                    {
                        error = "Incomplete extended TLV length.";
                        return false;
                    }
                    len = (buffer[i + 2] << 8) + buffer[i + 3];
                    i += 4 + len;
                }

                continue;
            }

            // NDEF-TLV (0x03)
            byte lengthByte = buffer[i + 1];
            int ndefLen;
            int payloadStart;

            if (lengthByte != 0xFF)
            {
                ndefLen = lengthByte;
                payloadStart = i + 2;
            }
            else
            {
                if (i + 3 >= buffer.Length)
                {
                    error = "Incomplete extended NDEF length field.";
                    return false;
                }
                ndefLen = (buffer[i + 2] << 8) + buffer[i + 3];
                payloadStart = i + 4;
            }

            if (payloadStart + ndefLen > buffer.Length)
            {
                error = "NDEF length exceeds buffer size.";
                return false;
            }

            ndefPayload = new byte[ndefLen];
            Array.Copy(buffer, payloadStart, ndefPayload, 0, ndefLen);
            return true;
        }

        error = "No valid NDEF TLV (0x03) found.";
        return false;
    }

    /// <summary>
    /// Baut eine NDEF-URI-Nachricht als TLV (0x03, LEN, NDEF..., 0xFE)
    /// und paddet auf blockSize (z.B. 4 Byte).
    /// </summary>
    public static byte[] CreateType5NdefTlv(string url, int blockSize)
    {
        if (blockSize <= 0)
            throw new ArgumentException("Block size must be > 0.", nameof(blockSize));

        var uriRecord = new NdefUriRecord { Uri = url };
        var ndefMessage = new NdefMessage { uriRecord };
        byte[] encoded = ndefMessage.ToByteArray();

        int msgLen = encoded.Length;
        var tlv = new List<byte>();

        tlv.Add(0x03); // NDEF TLV

        if (msgLen < 0xFF)
        {
            tlv.Add((byte)msgLen);
        }
        else
        {
            tlv.Add(0xFF);
            tlv.Add((byte)(msgLen >> 8));
            tlv.Add((byte)(msgLen & 0xFF));
        }

        tlv.AddRange(encoded);
        tlv.Add(0xFE); // Terminator

        int padding = (-tlv.Count) % blockSize;
        if (padding < 0) padding += blockSize;
        for (int i = 0; i < padding; i++)
            tlv.Add(0x00);

        return tlv.ToArray();
    }

    // ============================================================
    //  TYPE 5: NDEF lesen
    // ============================================================

    /// <summary>
    /// Liest UID und NDEF-Inhalt von einem Type-5 (ISO15693) Tag.
    /// Reader (ACR1552U) muss von außen per Connect(...) verbunden sein.
    /// </summary>
    public static bool ReadTagType5(
        SCardReader reader,
        string readerName,
        out string uid,
        out string[] records,
        out string status)
    {
        uid = string.Empty;
        records = Array.Empty<string>();
        status = string.Empty;

        if (reader == null || reader.ActiveProtocol == SCardProtocol.Unset)
        {
            status = $"Reader '{readerName}' is not connected (ActiveProtocol is Unset).";
            return false;
        }

        try
        {
            // 1) UID lesen
            uid = GetCardUid(reader);
            if (string.IsNullOrEmpty(uid))
            {
                status = $"Failed to read UID on reader '{readerName}'.";
                return false;
            }

            Console.WriteLine($"[Type5] UID={uid} on reader '{readerName}'");

            // 2) Speicherbereich lesen – wir versuchen verschiedene Längen
            byte[] raw = Array.Empty<byte>();
            string readStatus = string.Empty;
            bool readOk = false;

            // Versuche nacheinander 64, 32, 16, 8 Blöcke
            byte[] tryBlocksMinus1 = { 0x3F, 0x1F, 0x0F, 0x07 };

            foreach (var nbm1 in tryBlocksMinus1)
            {
                if (ReadIso15693MultipleBlocks(reader, 0x00, nbm1, out raw, out readStatus))
                {
                    readOk = true;
                    Console.WriteLine($"[Type5] Read success with numBlocksMinus1=0x{nbm1:X2}, bytes={raw.Length}");
                    break;
                }
                else
                {
                    Console.WriteLine($"[Type5] Read failed with numBlocksMinus1=0x{nbm1:X2}: {readStatus}");
                }
            }

            if (!readOk || raw == null || raw.Length == 0)
            {
                status = $"Could not read ISO15693 memory on reader '{readerName}': {readStatus}";
                return false;
            }

            Console.WriteLine("[Type5] Raw first 32 bytes: " +
                BitConverter.ToString(raw.Take(32).ToArray()));

            // 3) NDEF-TLV extrahieren
            if (!TryExtractNdefFromTlv(raw, out var encodedMessage, out string tlvError))
            {
                status = $"Could not extract NDEF TLV on reader '{readerName}': {tlvError}";
                return false;
            }

            Console.WriteLine($"[Type5] NDEF payload length: {encodedMessage.Length}");

            // 4) NDEF dekodieren
            NdefMessage ndefMessage;
            try
            {
                ndefMessage = NdefMessage.FromByteArray(encodedMessage);
            }
            catch (Exception ex)
            {
                status = $"Error decoding NDEF on reader '{readerName}': {ex.Message}";
                return false;
            }

            if (ndefMessage.Count == 0)
            {
                status = $"No NDEF records found on reader '{readerName}'.";
                return false;
            }

            var recList = new List<string>();

            foreach (var record in ndefMessage)
            {
                string desc;

                if (record is NdefUriRecord uriRecord)
                {
                    desc = uriRecord.Uri;
                }
                else if (record is NdefTextRecord textRecord)
                {
                    desc = textRecord.Text;
                }
                else
                {
                    string payloadStr = "";
                    if (record.Payload != null && record.Payload.Length > 0)
                    {
                        try
                        {
                            var candidate = Encoding.UTF8.GetString(record.Payload);
                            payloadStr = IsMostlyPrintable(candidate)
                                ? candidate
                                : BitConverter.ToString(record.Payload).Replace("-", "");
                        }
                        catch
                        {
                            payloadStr = BitConverter.ToString(record.Payload).Replace("-", "");
                        }
                    }

                    desc = payloadStr;
                }

                recList.Add(desc);
            }

            records = recList.ToArray();
            status = "OK";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Exception on reader '{readerName}': {ex.Message}";
            return false;
        }
    }

    // ============================================================
    //  TYPE 5: NDEF schreiben (vorsichtig benutzen!)
    // ============================================================

    /// <summary>
    /// Formatiert einen Type-5-Tag als NDEF-Tag und schreibt eine URI.
    /// ACHTUNG: Überschreibt ab CC-Block alle Daten.
    /// Annahmen:
    ///   - Blockgröße = 4 Byte
    ///   - CC in Block 0
    ///   - NDEF-TLV ab Block 1
    /// </summary>
    public static bool FormatAndWriteNdefType5(
        SCardReader reader,
        string readerName,
        string url,
        out string status)
    {
        status = string.Empty;

        const int blockSize = 4;
        const byte ccBlock = 0;
        const byte ndefStartBlock = 1;

        if (reader == null || reader.ActiveProtocol == SCardProtocol.Unset)
        {
            status = $"Reader '{readerName}' is not connected (ActiveProtocol is Unset).";
            return false;
        }

        if (string.IsNullOrEmpty(url))
        {
            status = "URL is null or empty.";
            return false;
        }

        try
        {
            string uid = GetCardUid(reader);
            Console.WriteLine($"[Type5-Write] UID={uid} on reader '{readerName}'");

            // 1) NDEF-TLV bauen
            byte[] tlv = CreateType5NdefTlv(url, blockSize);
            int ndefBytes = tlv.Length;

            // 2) CC MLen berechnen:
            //    NDEF-Bereich beginnt bei Block 1 => Offset = 1 * 4
            int firstNdefByteOffset = ndefStartBlock * blockSize;
            int lastNdefByteOffset = firstNdefByteOffset + ndefBytes;

            // CC MLen rechnet in 8-Byte-Einheiten; (MLen+1)*8 = Bytes
            int memSizeBytes = (int)Math.Ceiling(lastNdefByteOffset / 8.0) * 8;
            int mlenUnits = memSizeBytes / 8;
            if (mlenUnits == 0) mlenUnits = 1;

            byte mlen = (byte)(mlenUnits - 1);

            // 3) CC-Block 0 schreiben: E1 40 MLen 00
            byte[] cc = new byte[blockSize];
            cc[0] = 0xE1;         // Magic
            cc[1] = 0x40;         // Mapping 1.0, RW
            cc[2] = mlen;         // NDEF-Mem in 8-Byte-Einheiten - 1
            cc[3] = 0x00;         // Features (einfach)

            if (!WriteIso15693SingleBlock(reader, ccBlock, cc, out string ccStatus))
            {
                status = $"Failed to write CC block {ccBlock} on reader '{readerName}': {ccStatus}";
                return false;
            }

            // 4) NDEF-TLV ab Block 1 schreiben
            int offset = 0;
            byte currentBlock = ndefStartBlock;

            while (offset < tlv.Length)
            {
                byte[] blockData = new byte[blockSize];
                int remaining = tlv.Length - offset;
                int copyLen = Math.Min(blockSize, remaining);
                Array.Copy(tlv, offset, blockData, 0, copyLen);

                if (!WriteIso15693SingleBlock(reader, currentBlock, blockData, out string blockStatus))
                {
                    status = $"Failed to write NDEF block {currentBlock} on reader '{readerName}': {blockStatus}";
                    return false;
                }

                offset += copyLen;
                currentBlock++;
            }

            status = "OK";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Exception while writing Type-5 NDEF on reader '{readerName}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Findet die Byte-Position des ersten gültigen NDEF-TLV (Type 0x03)
    /// im gegebenen Speicherpuffer.
    /// - überspringt optional Type-5-CC (E1 40 .. ..)
    /// </summary>
    private static bool TryFindNdefTlvOffset(
        byte[] buffer,
        out int tlvOffset,
        out string error)
    {
        tlvOffset = -1;
        error = string.Empty;

        if (buffer == null || buffer.Length < 3)
        {
            error = "Buffer too small for TLV.";
            return false;
        }

        int i = 0;

        // Optional: Capability Container (CC) am Anfang überspringen
        if (buffer[0] == 0xE1 && buffer.Length >= 4)
        {
            // E1 40 ML EN Feature
            i = 4;
        }

        while (i < buffer.Length)
        {
            byte t = buffer[i];

            if (t == 0x00)
            {
                // NULL TLV
                i += 1;
                continue;
            }

            if (t == 0xFE)
            {
                // Terminator TLV
                break;
            }

            if (i + 1 >= buffer.Length)
            {
                error = "Incomplete TLV length field.";
                return false;
            }

            if (t == 0x03)
            {
                // Gefundener NDEF-TLV
                tlvOffset = i;
                return true;
            }

            // Anderer TLV-Typ -> überspringen
            byte lenByte = buffer[i + 1];
            int len;
            if (lenByte != 0xFF)
            {
                len = lenByte;
                i += 2 + len;
            }
            else
            {
                if (i + 3 >= buffer.Length)
                {
                    error = "Incomplete extended TLV length.";
                    return false;
                }
                len = (buffer[i + 2] << 8) + buffer[i + 3];
                i += 4 + len;
            }
        }

        error = "No NDEF TLV (0x03) found.";
        return false;
    }

    /// <summary>
    /// Überschreibt den vorhandenen NDEF-TLV eines Type-5-Tags an Ort und Stelle,
    /// ohne CC oder andere Bereiche anzufassen.
    /// Der Tag muss bereits als NFC Forum Type 5 formatiert sein.
    /// </summary>
    public static bool OverwriteNdefType5InPlace(
        SCardReader reader,
        string readerName,
        string url,
        out string status)
    {
        status = string.Empty;

        const int blockSize = 4; // SLIX2: 4-Byte-Blöcke

        if (reader == null || reader.ActiveProtocol == SCardProtocol.Unset)
        {
            status = $"Reader '{readerName}' is not connected (ActiveProtocol is Unset).";
            return false;
        }

        if (string.IsNullOrEmpty(url))
        {
            status = "URL is null or empty.";
            return false;
        }

        try
        {
            string uid = GetCardUid(reader);
            Console.WriteLine($"[Type5-Overwrite] UID={uid} on reader '{readerName}'");

            // 1) Speicherbereich lesen (ähnlich wie ReadTagType5)
            byte[] raw = Array.Empty<byte>();
            string readStatus = string.Empty;
            bool readOk = false;

            // Versuche nacheinander 64, 32, 16, 8 Blöcke
            byte[] tryBlocksMinus1 = { 0x3F, 0x1F, 0x0F, 0x07 };

            foreach (var nbm1 in tryBlocksMinus1)
            {
                if (ReadIso15693MultipleBlocks(reader, 0x00, nbm1, out raw, out readStatus))
                {
                    readOk = true;
                    Console.WriteLine($"[Type5-Overwrite] Read success with numBlocksMinus1=0x{nbm1:X2}, bytes={raw.Length}");
                    break;
                }
                else
                {
                    Console.WriteLine($"[Type5-Overwrite] Read failed with numBlocksMinus1=0x{nbm1:X2}: {readStatus}");
                }
            }

            if (!readOk || raw == null || raw.Length == 0)
            {
                status = $"Could not read ISO15693 memory on reader '{readerName}': {readStatus}";
                return false;
            }

            Console.WriteLine("[Type5-Overwrite] Raw first 32 bytes: " +
                BitConverter.ToString(raw.Take(32).ToArray()));

            // 2) NDEF-TLV-Offset finden
            if (!TryFindNdefTlvOffset(raw, out int tlvOffset, out string tlvError))
            {
                status = $"Could not find NDEF TLV on reader '{readerName}': {tlvError}";
                return false;
            }

            Console.WriteLine($"[Type5-Overwrite] NDEF TLV at offset {tlvOffset}");

            // 3) Neuen NDEF-TLV für die gewünschte URL bauen
            byte[] newTlv = CreateType5NdefTlv(url, blockSize);

            if (newTlv.Length > raw.Length - tlvOffset)
            {
                status = $"New NDEF TLV ({newTlv.Length} bytes) does not fit in remaining memory ({raw.Length - tlvOffset} bytes).";
                return false;
            }

            // 4) Neuen TLV in den Puffer kopieren
            Array.Copy(newTlv, 0, raw, tlvOffset, newTlv.Length);

            // 5) Nur die betroffenen Blöcke zurückschreiben
            int firstBlock = tlvOffset / blockSize;
            int lastByteIndex = tlvOffset + newTlv.Length - 1;
            int lastBlock = lastByteIndex / blockSize;

            Console.WriteLine($"[Type5-Overwrite] Writing blocks {firstBlock}..{lastBlock}");

            for (int block = firstBlock; block <= lastBlock; block++)
            {
                int byteIndex = block * blockSize;

                // Sicherheitscheck
                if (byteIndex + blockSize > raw.Length)
                    break;

                byte[] blockData = new byte[blockSize];
                Array.Copy(raw, byteIndex, blockData, 0, blockSize);

                if (!WriteIso15693SingleBlock(reader, (byte)block, blockData, out string writeStatus))
                {
                    status = $"Failed to write block {block} on reader '{readerName}': {writeStatus}";
                    return false;
                }
            }

            status = "OK";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Exception while overwriting NDEF on reader '{readerName}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Formatiert einen bestehenden NFC Forum Type 5 (ISO15693) Tag neu als NDEF-Tag
    /// und schreibt einen leeren NDEF-TLV (03 00 FE ...).
    /// 
    /// Achtung:
    /// - Überschreibt ab Block 0 (CC) und danach die NDEF-TLV-Blöcke.
    /// - Reader (ACR1552U / WalletMate II) muss von außen per Connect(...) verbunden sein.
    /// </summary>
    /// <summary>
    /// Formatiert einen bestehenden NFC Forum Type 5 (ISO15693) Tag neu als NDEF-Tag
    /// und schreibt einen leeren NDEF-Record (TNF=Empty) als Platzhalter:
    /// NDEF-Message = D0 00 00  (MB=1, ME=1, SR=1, TNF=Empty)
    /// NDEF-TLV    = 03 03 D0 00 00 FE (+ Padding)
    /// 
    /// Achtung:
    /// - Überschreibt ab Block 0 (CC) und ab Block 1 den NDEF-Bereich.
    /// - Reader (ACR1552U / WalletMate II) muss von außen per Connect(...) verbunden sein.
    /// </summary>
    /// <summary>
    /// Formatiert einen bestehenden NFC Forum Type 5 (ISO15693) Tag neu als NDEF-Tag
    /// und schreibt einen leeren NDEF-Record (TNF=Empty) als Platzhalter:
    /// NDEF-Message = D0 00 00  (MB=1, ME=1, SR=1, TNF=Empty)
    /// NDEF-TLV    = 03 03 D0 00 00 FE (+ Padding)
    /// 
    /// Achtung:
    /// - Überschreibt ab Block 0 (CC) und ab Block 1 den NDEF-Bereich.
    /// - Reader (ACR1552U / WalletMate II) muss von außen per Connect(...) verbunden sein.
    /// </summary>
    /// <summary>
    /// Formatiert einen bestehenden NFC Forum Type 5 (ISO15693) Tag neu als NDEF-Tag
    /// und schreibt einen leeren NDEF-Record (TNF=Empty) als Platzhalter:
    /// NDEF-Message = D0 00 00  (MB=1, ME=1, SR=1, TNF=Empty)
    /// NDEF-TLV    = 03 03 D0 00 00 FE (+ Padding)
    /// 
    /// Achtung:
    /// - Überschreibt CC in Block 0 und den NDEF-Bereich ab Block 1.
    /// - Reader (ACR1552U / WalletMate II) muss von außen per Connect(...) verbunden sein.
    /// </summary>
    public static bool FormatType5AsEmptyNdef(
        SCardReader reader,
        string readerName,
        out string status)
    {
        status = string.Empty;

        const int blockSize = 4;   // SLIX2: 4 Byte pro Block
        const byte ccBlock = 0;   // CC in Block 0
        const byte ndefStartBlock = 1;   // NDEF-TLV ab Block 1

        if (reader == null || reader.ActiveProtocol == SCardProtocol.Unset)
        {
            status = $"Reader '{readerName}' is not connected (ActiveProtocol is Unset).";
            return false;
        }

        try
        {
            string uid = GetCardUid(reader);
            Console.WriteLine($"[FormatType5] UID={uid} on reader '{readerName}'");

            // 1) Leeren NDEF-Record (Empty TNF) als NDEF-Message:
            //    NDEF = D0 00 00
            byte[] emptyNdefMessage = new byte[] { 0xD0, 0x00, 0x00 };

            //    NDEF-TLV: 03 03 D0 00 00 FE (+ Padding)
            var tlv = new List<byte>();
            tlv.Add(0x03);                // NDEF-TLV
            tlv.Add(0x03);                // Länge = 3 Bytes NDEF-Message
            tlv.AddRange(emptyNdefMessage);
            tlv.Add(0xFE);                // Terminator

            // Padding auf Blockgröße
            int padding = (-tlv.Count) % blockSize;
            if (padding < 0) padding += blockSize;
            for (int i = 0; i < padding; i++)
                tlv.Add(0x00);

            byte[] tlvBytes = tlv.ToArray();
            int ndefBytes = tlvBytes.Length;

            // 2) CC einstellen wie die Smartphone-App:
            //    SLIX2: 320 Bytes User Memory -> MLen = 320 / 8 = 40 = 0x28
            //    Features: 0x01 (multiple block read supported)
            const int totalNdefBytes = 320;  // für deinen SLIX2
            byte mlen = (byte)(totalNdefBytes / 8);  // 320 / 8 = 40 = 0x28

            byte[] cc = new byte[blockSize];
            cc[0] = 0xE1; // Magic (NFC Forum)
            cc[1] = 0x40; // NDEF Mapping 1.0, RW
            cc[2] = mlen; // NDEF-Größe in 8-Byte-Einheiten
            cc[3] = 0x01; // Feature Flags -> Multiple Block Read enabled

            // 3) CC in Block 0 schreiben
            if (!WriteIso15693SingleBlock(reader, ccBlock, cc, out string ccStatus))
            {
                status = $"Failed to write CC block {ccBlock} on reader '{readerName}': {ccStatus}";
                return false;
            }

            // 4) NDEF-TLV (mit leerem Record) ab Block 1 schreiben
            int offset = 0;
            byte currentBlock = ndefStartBlock;

            while (offset < tlvBytes.Length)
            {
                byte[] blockData = new byte[blockSize];
                int remaining = tlvBytes.Length - offset;
                int copyLen = Math.Min(blockSize, remaining);
                Array.Copy(tlvBytes, offset, blockData, 0, copyLen);

                if (!WriteIso15693SingleBlock(reader, currentBlock, blockData, out string writeStatus))
                {
                    status = $"Failed to write NDEF-TLV block {currentBlock} on reader '{readerName}': {writeStatus}";
                    return false;
                }

                offset += copyLen;
                currentBlock += 1;
            }

            status = "OK";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Exception in FormatType5AsEmptyNdef on reader '{readerName}': {ex.Message}";
            return false;
        }
    }

    public static void ReverseUidHexToDecimal_FromGetCardUid(
        string uidHex,             // format: "04A1CCB1320289"
        out string reversedHex,
        out BigInteger decimalValue)
    {
        reversedHex = string.Empty;
        decimalValue = BigInteger.Zero;

        if (string.IsNullOrWhiteSpace(uidHex))
            return;

        // Input is already hex without separators ("04A1CCB1320289")
        uidHex = uidHex.ToUpperInvariant();

        if (uidHex.Length % 2 != 0)
            throw new ArgumentException("UID hex string must have an even number of characters.", nameof(uidHex));

        // Convert UID hex → byte array
        int byteCount = uidHex.Length / 2;
        byte[] bytes = new byte[byteCount];

        for (int i = 0; i < byteCount; i++)
        {
            bytes[i] = Convert.ToByte(uidHex.Substring(i * 2, 2), 16);
        }

        // Reverse byte order
        Array.Reverse(bytes);

        // Convert reversed bytes → hex string
        reversedHex = BitConverter.ToString(bytes).Replace("-", "");

        // Convert reversed hex to decimal (BigInteger to avoid overflow)
        decimalValue = BigInteger.Parse("0" + reversedHex, System.Globalization.NumberStyles.HexNumber);
    }
}