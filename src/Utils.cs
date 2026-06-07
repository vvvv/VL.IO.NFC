using NdefLibrary.Ndef;
using PCSC;
using PCSC.Iso7816;
using PCSC.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Numerics;

namespace IO.NFC;

public static class Utils
{
    // ============================================================
    //  Hilfsfunktionen
    // ============================================================

    private static bool IsMostlyPrintable(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int printable = 0;
        foreach (char c in s)
        {
            if (c == '\r' || c == '\n' || c == '\t' || (c >= 32 && c < 127))
                printable++;
        }
        return ((double)printable / s.Length) >= 0.8;
    }

    public static string[] ListAllReaders()
    {
        var contextFactory = ContextFactory.Instance;
        using var context = contextFactory.Establish(SCardScope.System);
        var readers = context.GetReaders();
        if (readers == null || readers.Length == 0) return Array.Empty<string>();
        return readers;
    }

    public static string GetFirstReader(ISCardContext context)
    {
        var readers = context.GetReaders();
        if (readers == null || readers.Length == 0)
            throw new Exception("No smart card readers found.");
        return readers[0];
    }

    public static ResponseApdu TransmitApdu(SCardReader reader, CommandApdu apdu)
    {
        byte[] sendBuffer = apdu.ToArray();
        IntPtr sendPci = SCardPCI.GetPci(reader.ActiveProtocol);
        var receivePci = new SCardPCI();
        byte[] receiveBuffer = new byte[258];
        int receiveLength = receiveBuffer.Length;

        var rc = reader.Transmit(sendPci, sendBuffer, sendBuffer.Length, receivePci, receiveBuffer, ref receiveLength);
        if (rc != SCardError.Success) throw new Exception("APDU transmit error: " + SCardHelper.StringifyError(rc));

        return new ResponseApdu(receiveBuffer, receiveLength, apdu.Case, reader.ActiveProtocol);
    }

    public static string GetCardUid(SCardReader reader)
    {
        var apdu = new CommandApdu(IsoCase.Case2Short, reader.ActiveProtocol)
        { CLA = 0xFF, INS = 0xCA, P1 = 0x00, P2 = 0x00, Le = 0x00 };

        try
        {
            var response = TransmitApdu(reader, apdu);
            if (response.SW1 == 0x90 && response.SW2 == 0x00)
            {
                var data = response.GetData();
                if (data != null && data.Length > 0)
                    return BitConverter.ToString(data).Replace("-", "").ToUpperInvariant();
            }
        }
        catch { }
        return string.Empty;
    }

    public static void ReverseHexToDecimal(string hexInput, out string reversedHex, out long decimalValue)
    {
        reversedHex = string.Empty; decimalValue = 0;
        if (string.IsNullOrEmpty(hexInput)) return;
        hexInput = hexInput.Replace(" ", "").ToUpperInvariant();
        if (hexInput.Length % 2 != 0) return;
        int byteCount = hexInput.Length / 2;
        byte[] bytes = new byte[byteCount];
        for (int i = 0; i < byteCount; i++) bytes[i] = Convert.ToByte(hexInput.Substring(i * 2, 2), 16);
        Array.Reverse(bytes);
        reversedHex = BitConverter.ToString(bytes).Replace("-", "");
        decimalValue = Convert.ToInt64(reversedHex, 16);
    }

    public static string BuildUrlWithUid(string baseUrl, string uid)
    {
        if (string.IsNullOrEmpty(uid)) return baseUrl;
        string sep = baseUrl.Contains('?') ? "&" : "?";
        return baseUrl + sep + "uid=" + uid;
    }

    // ============================================================
    //  LOW-LEVEL SCHREIBEN/LESEN (Tolerant für Type 5)
    // ============================================================

    public static bool ReadIso15693MultipleBlocks(SCardReader reader, byte firstBlock, byte maxBlocks, out byte[] data, out string status)
    {
        data = Array.Empty<byte>(); status = string.Empty;
        var allData = new List<byte>();

        for (int b = 0; b < maxBlocks; b++)
        {
            byte currentBlock = (byte)(firstBlock + b);
            var apdu = new CommandApdu(IsoCase.Case3Short, reader.ActiveProtocol)
            { CLA = 0xFF, INS = 0xFB, P1 = 0x00, P2 = 0x00, Data = new byte[] { 0x20, currentBlock } };

            try
            {
                var response = TransmitApdu(reader, apdu);
                if (response.SW1 == 0x90 && response.SW2 == 0x00)
                {
                    var blockData = response.GetData();
                    if (blockData != null) allData.AddRange(blockData);
                }
                else
                {
                    break; // Physisches Ende des Speichers erreicht
                }
            }
            catch
            {
                // VERBINDUNGSABBRUCH (Error 1112 / Tag Tearing)
                // Wir brechen die Schleife ab, behalten aber alle bisher gelesenen Daten!
                break;
            }
        }

        data = allData.ToArray();
        if (data.Length > 0)
        {
            status = "OK";
            return true;
        }

        status = "Could not read any blocks. Tag removed too early?";
        return false;
    }

    public static bool WriteIso15693SingleBlock(SCardReader reader, byte blockNumber, byte[] blockData, out string status)
    {
        status = string.Empty;
        if (blockData == null || blockData.Length == 0) return false;

        byte[] data = new byte[2 + blockData.Length];
        data[0] = 0x21; data[1] = blockNumber;
        Array.Copy(blockData, 0, data, 2, blockData.Length);

        var apdu = new CommandApdu(IsoCase.Case3Short, reader.ActiveProtocol)
        { CLA = 0xFF, INS = 0xFB, P1 = 0x00, P2 = 0x00, Data = data };

        try
        {
            var response = TransmitApdu(reader, apdu);
            if (response.SW1 == 0x90 && response.SW2 == 0x00) { status = "OK"; return true; }
            status = $"Write failed SW1={response.SW1:X2}"; return false;
        }
        catch (Exception ex) { status = "Exception: " + ex.Message; return false; }
    }

    // ============================================================
    //  NDEF / TLV
    // ============================================================

    private static bool TryExtractNdefFromTlv(byte[] buffer, out byte[] ndefPayload, out string error)
    {
        ndefPayload = Array.Empty<byte>(); error = string.Empty;
        if (buffer == null || buffer.Length < 3) return false;

        for (int i = 0; i < buffer.Length - 2; i++)
        {
            if (buffer[i] == 0x03) // NDEF TLV
            {
                int len = buffer[i + 1];
                int payloadStart = i + 2;

                if (len == 0xFF) // Extended Length
                {
                    if (i + 4 > buffer.Length) break;
                    len = (buffer[i + 2] << 8) + buffer[i + 3];
                    payloadStart = i + 4;
                }

                if (payloadStart + len <= buffer.Length)
                {
                    ndefPayload = new byte[len];
                    Array.Copy(buffer, payloadStart, ndefPayload, 0, len);
                    return true;
                }
            }
        }
        error = "No NDEF found."; return false;
    }

    public static byte[] CreateType5NdefTlv(string url, int blockSize)
    {
        var uriRecord = new NdefUriRecord { Uri = url };
        var ndefMessage = new NdefMessage { uriRecord };
        byte[] encoded = ndefMessage.ToByteArray();

        var tlv = new List<byte> { 0x03 };
        if (encoded.Length < 0xFF) tlv.Add((byte)encoded.Length);
        else { tlv.Add(0xFF); tlv.Add((byte)(encoded.Length >> 8)); tlv.Add((byte)(encoded.Length & 0xFF)); }

        tlv.AddRange(encoded);
        tlv.Add(0xFE); // Terminator

        int padding = (-tlv.Count) % blockSize;
        if (padding < 0) padding += blockSize;
        for (int i = 0; i < padding; i++) tlv.Add(0x00);

        return tlv.ToArray();
    }

    // ============================================================
    //  VVVV NODES
    // ============================================================

    public static bool ReadTagType5(SCardReader reader, string readerName, out string uid, out string[] records, out string status)
    {
        uid = string.Empty; records = Array.Empty<string>(); status = string.Empty;

        if (reader == null || reader.ActiveProtocol == SCardProtocol.Unset)
        {
            status = "Reader not connected."; return false;
        }

        try
        {
            uid = GetCardUid(reader);
            if (string.IsNullOrEmpty(uid)) return false;

            // Tolerantes Auslesen von maximal 40 Blöcken (reicht für die meisten SLIX Tags)
            byte[] rawMemory = Array.Empty<byte>();
            if (!ReadIso15693MultipleBlocks(reader, 0x00, 40, out rawMemory, out status))
                return false;

            if (!TryExtractNdefFromTlv(rawMemory, out var encodedMessage, out string tlvError))
            {
                status = tlvError; return false;
            }

            var ndefMessage = NdefMessage.FromByteArray(encodedMessage);
            var recList = new List<string>();

            // Saubere Dekodierung von Texten und URLs
            foreach (var record in ndefMessage)
            {
                if (record.TypeNameFormat == NdefRecord.TypeNameFormatType.NfcRtd && record.Type != null && record.Type.Length > 0)
                {
                    if (record.Type[0] == 0x55) // 'U' für URI
                    {
                        var uriRecord = new NdefUriRecord(record);
                        recList.Add(uriRecord.Uri);
                    }
                    else if (record.Type[0] == 0x54) // 'T' für Text
                    {
                        var textRecord = new NdefTextRecord(record);
                        recList.Add(textRecord.Text);
                    }
                    else
                    {
                        recList.Add(BitConverter.ToString(record.Payload ?? Array.Empty<byte>()));
                    }
                }
                else
                {
                    recList.Add(BitConverter.ToString(record.Payload ?? Array.Empty<byte>()));
                }
            }

            records = recList.ToArray();
            status = "OK";
            return true;
        }
        catch (Exception ex) { status = ex.Message; return false; }
    }

    public static bool FormatAndWriteNdefType5(SCardReader reader, string readerName, string url, out string status)
    {
        status = string.Empty;
        const int blockSize = 4;

        if (reader == null || reader.ActiveProtocol == SCardProtocol.Unset)
        {
            status = "Reader not connected."; return false;
        }

        try
        {
            byte[] tlv = CreateType5NdefTlv(url, blockSize);
            int offset = 0;

            // Type 5 Logik: CC in Block 0, dann NDEF ab Block 1
            int memSizeBytes = (int)Math.Ceiling((4 + tlv.Length) / 8.0) * 8;
            if (memSizeBytes < 32) memSizeBytes = 32;

            // Capability Container schreiben
            byte[] cc = { 0xE1, 0x40, (byte)(memSizeBytes / 8), 0x01 };
            if (!WriteIso15693SingleBlock(reader, 0, cc, out string ccStatus))
            {
                status = ccStatus; return false;
            }

            // NDEF Daten schreiben
            byte currentBlock = 1;
            while (offset < tlv.Length)
            {
                byte[] blockData = new byte[blockSize];
                int copyLen = Math.Min(blockSize, tlv.Length - offset);
                Array.Copy(tlv, offset, blockData, 0, copyLen);

                if (!WriteIso15693SingleBlock(reader, currentBlock, blockData, out string blockStatus))
                {
                    status = blockStatus; return false;
                }
                offset += copyLen;
                currentBlock++;
            }

            status = "OK"; return true;
        }
        catch (Exception ex) { status = ex.Message; return false; }
    }

    // ============================================================
    //  Restliche Original-Nodes
    // ============================================================

    public static bool OverwriteNdefType5InPlace(SCardReader reader, string readerName, string url, out string status)
    {
        return FormatAndWriteNdefType5(reader, readerName, url, out status);
    }

    public static bool FormatType5AsEmptyNdef(SCardReader reader, string readerName, out string status)
    {
        return FormatAndWriteNdefType5(reader, readerName, "", out status);
    }

    public static bool TestWriteReadBlockType5(SCardReader reader, string readerName, byte blockNumber, out byte[] readBack, out string status)
    {
        readBack = Array.Empty<byte>(); status = string.Empty; return false;
    }

    public static void ReverseUidHexToDecimal_FromGetCardUid(string uidHex, out string reversedHex, out BigInteger decimalValue)
    {
        reversedHex = string.Empty; decimalValue = BigInteger.Zero;
        if (string.IsNullOrWhiteSpace(uidHex)) return;
        uidHex = uidHex.ToUpperInvariant();
        if (uidHex.Length % 2 != 0) return;
        int byteCount = uidHex.Length / 2;
        byte[] bytes = new byte[byteCount];
        for (int i = 0; i < byteCount; i++) bytes[i] = Convert.ToByte(uidHex.Substring(i * 2, 2), 16);
        Array.Reverse(bytes);
        reversedHex = BitConverter.ToString(bytes).Replace("-", "");
        decimalValue = BigInteger.Parse("0" + reversedHex, System.Globalization.NumberStyles.HexNumber);
    }
}