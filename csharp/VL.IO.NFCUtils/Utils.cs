using NdefLibrary.Ndef;
using PCSC;
using PCSC.Iso7816;
using PCSC.Utils;
using System;
using System.Collections.Generic;
using System.Text;

namespace IO.NFC;

public static class Utils
{

    /// <summary>
    /// Takes a hex string (e.g. "04A1CCB1320289"), reverses the byte order,
    /// and converts the reversed hex to decimal.
    /// </summary>
    /// <param name="hexInput">Original hex string (e.g. UID from NFC tag).</param>
    /// <param name="reversedHex">Output: reversed hex string.</param>
    /// <param name="decimalValue">Output: decimal value of the reversed hex.</param>
    public static void ReverseHexToDecimal(
        string hexInput,
        out string reversedHex,
        out long decimalValue)
    {
        reversedHex = string.Empty;
        decimalValue = 0;

        if (string.IsNullOrEmpty(hexInput))
            return;

        // 1) Normalize: remove spaces, uppercase
        hexInput = hexInput.Replace(" ", "").ToUpperInvariant();

        // 2) Ensure even length
        if (hexInput.Length % 2 != 0)
            throw new ArgumentException("Hex string must have an even number of characters.", nameof(hexInput));

        // 3) Hex -> bytes
        int byteCount = hexInput.Length / 2;
        byte[] bytes = new byte[byteCount];
        for (int i = 0; i < byteCount; i++)
        {
            bytes[i] = Convert.ToByte(hexInput.Substring(i * 2, 2), 16);
        }

        // 4) Reverse bytes
        Array.Reverse(bytes);

        // 5) Bytes -> reversed hex string
        reversedHex = BitConverter.ToString(bytes).Replace("-", "");

        // 6) Reversed hex -> decimal
        // (Assuming it fits in Int64; for very long UIDs you'd use BigInteger)
        decimalValue = Convert.ToInt64(reversedHex, 16);
    }





    /// <summary>
    /// Liest UID und NDEF-Inhalt von einem bereits verbundenen NFC-Reader.
    /// Der SCardReader muss von außen erstellt und über Connect(...) verbunden worden sein.
    /// 
    /// Outputs:
    ///   uid     - Tag UID als Hexstring (z.B. "04A1CCB1320289")
    ///   records - Liste von lesbaren Strings:
    ///             * URI-Records -> "https://...."
    ///             * Text-Records -> "Hello World"
    ///             * Sonstige -> Payload als Text (falls druckbar) oder Hex
    ///   status  - "OK" oder Fehlermeldung (inkl. ReaderName)
    /// 
    /// Rückgabewert:
    ///   true  -> Lesen erfolgreich
    ///   false -> Fehler (Details in status)
    /// </summary>
    public static bool ReadTag(
        SCardReader reader,
        string readerName,
        out string uid,
        out string[] records,
        out string status)
    {
        uid = string.Empty;
        records = Array.Empty<string>();
        status = string.Empty;

        try
        {
            // 1) UID lesen
            uid = GetCardUid(reader);
            if (string.IsNullOrEmpty(uid))
            {
                status = $"Failed to read UID on reader '{readerName}'.";
                return false;
            }

            // 2) TLV ab Page 4 lesen (NDEF-TLV-Bereich)
            var tlvBytes = new List<byte>(128);
            var apdu = new CommandApdu(IsoCase.Case2Short, reader.ActiveProtocol)
            {
                CLA = 0xFF,
                INS = 0xB0,
                P1 = 0x00,
                P2 = 0x04, // Start bei Page 4
                Le = 0x04
            };

            while (true)
            {
                var response = TransmitApdu(reader, apdu);

                if (response.SW1 == 0x90 && response.SW2 == 0x00)
                {
                    var data = response.GetData();
                    if (data == null || data.Length == 0)
                        break;

                    tlvBytes.AddRange(data);

                    // Stopp, wenn Terminator-TLV (0xFE) in dieser Page auftaucht
                    if (Array.IndexOf(data, (byte)0xFE) >= 0)
                        break;

                    apdu.P2++; // nächste Page
                }
                else
                {
                    status = $"Failed to read page {apdu.P2} on reader '{readerName}', SW1={response.SW1:X2}, SW2={response.SW2:X2}";
                    return false;
                }
            }

            if (tlvBytes.Count < 3)
            {
                status = $"No TLV data read from tag on reader '{readerName}'.";
                return false;
            }

            var tlv = tlvBytes.ToArray();

            // 3) TLV: 0x03 LEN [NDEF bytes] 0xFE
            if (tlv[0] != 0x03)
            {
                status = $"No NDEF TLV (0x03) at start of data on reader '{readerName}'.";
                return false;
            }

            int ndefLength = tlv[1];
            int ndefStart = 2;
            int ndefEnd = ndefStart + ndefLength;

            if (ndefEnd > tlv.Length)
            {
                status = $"NDEF length exceeds TLV size on reader '{readerName}'.";
                return false;
            }

            var encodedMessage = new byte[ndefLength];
            Array.Copy(tlv, ndefStart, encodedMessage, 0, ndefLength);

            // 4) NDEF-Nachricht dekodieren
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

            // 5) Records in lesbare Strings umwandeln
            var recList = new List<string>();

            foreach (var record in ndefMessage)
            {
                string desc;

                if (record is NdefUriRecord uriRecord)
                {
                    // Nur die fertige URI
                    desc = uriRecord.Uri;
                }
                else if (record is NdefTextRecord textRecord)
                {
                    // Nur der Text
                    desc = textRecord.Text;
                }
                else
                {
                    // Generischer Fallback: Payload als UTF8 oder Hex
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
                printable++;
        }

        double ratio = (double)printable / s.Length;
        return ratio >= 0.8;
    }



    /// <summary>
    /// Returns a list of all connected NFC reader names (smart card readers).
    /// </summary>
    public static string[] ListAllReaders()
    {
        var contextFactory = ContextFactory.Instance;
        using (var context = contextFactory.Establish(SCardScope.System))
        {
            var readers = context.GetReaders();

            if (readers == null || readers.Length == 0)
            {
                Console.WriteLine("No NFC readers found. Please check driver installation.");
                return Array.Empty<string>();
            }

            // Print to console for debugging (optional)
            Console.WriteLine("Available NFC Readers:");
            foreach (var reader in readers)
            {
                Console.WriteLine(" - " + reader);
            }

            return readers;
        }
    }


    /// <summary>
    /// Returns the first available NFC reader name.
    /// </summary>
    public static string GetFirstReader(ISCardContext context)
    {
        var readers = context.GetReaders();
        if (readers == null || readers.Length == 0)
        {
            throw new Exception("No smart card readers found. Is your NFC reader driver installed?");
        }

        // You can filter for "ACR122" here if you like.
        return readers[0];
    }

    /// <summary>
    /// Sends an APDU command and returns the response.
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
        {
            throw new Exception("APDU transmit error: " + SCardHelper.StringifyError(rc));
        }

        return new ResponseApdu(
            receiveBuffer,
            receiveLength,
            apdu.Case,
            reader.ActiveProtocol);
    }

    /// <summary>
    /// Reads the NFC tag UID using the APDU FF CA 00 00 00 command.
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
                string uidWithSpaces = BitConverter.ToString(data).Replace('-', ' ');
                string uidCompact = uidWithSpaces.Replace(" ", "").ToUpperInvariant();
                return uidCompact;
            }
        }

        Console.WriteLine("Failed to read UID, SW1={0:X2}, SW2={1:X2}", response.SW1, response.SW2);
        return "";
    }

    /// <summary>
    /// Builds a URL with the UID appended as a query parameter.
    /// </summary>
    public static string BuildUrlWithUid(string baseUrl, string uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            Console.WriteLine("No UID available, writing base URL without uid parameter.");
            return baseUrl;
        }

        string separator = baseUrl.Contains("?") ? "&" : "?";
        return baseUrl + separator + "uid=" + uid;
    }

    /// <summary>
    /// Creates an NDEF TLV block containing a URI record.
    /// </summary>
    public static byte[] CreateNdefTlv(string url)
    {
        var uriRecord = new NdefUriRecord { Uri = url };
        var ndefMessage = new NdefMessage { uriRecord };
        byte[] encodedMessage = ndefMessage.ToByteArray();

        int messageLength = encodedMessage.Length;

        var initial = new List<byte>();
        initial.Add(0x03);
        initial.Add((byte)messageLength);
        initial.AddRange(encodedMessage);
        initial.Add(0xFE);

        int padding = (-initial.Count) % 4;
        if (padding < 0) padding += 4;
        for (int i = 0; i < padding; i++)
        {
            initial.Add(0x00);
        }

        return initial.ToArray();
    }

    /// <summary>
    /// Writes an NDEF message to the NFC tag, starting at page 4.
    /// </summary>
    public static bool WriteNdefMessage(SCardReader reader, byte[] ndefBytes)
    {
        int page = 4;
        int index = 0;

        while (index < ndefBytes.Length)
        {
            byte[] block = new byte[4];
            int remaining = ndefBytes.Length - index;
            int copyLen = Math.Min(4, remaining);
            Array.Copy(ndefBytes, index, block, 0, copyLen);

            var apdu = new CommandApdu(IsoCase.Case3Short, reader.ActiveProtocol)
            {
                CLA = 0xFF,
                INS = 0xD6,
                P1 = 0x00,
                P2 = (byte)page,
                Data = block
            };

            var response = TransmitApdu(reader, apdu);

            if (response.SW1 != 0x90 || response.SW2 != 0x00)
            {
                Console.WriteLine(
                    "Failed to write to page {0}, SW1={1:X2}, SW2={2:X2}",
                    page, response.SW1, response.SW2);
                return false;
            }

            page++;
            index += 4;
        }

        return true;
    }

    /// <summary>
    /// Reads a 4-byte page from the tag (useful for inspecting lock bytes).
    /// </summary>
    public static byte[] ReadPage(SCardReader reader, byte page)
    {
        var apdu = new CommandApdu(IsoCase.Case2Short, reader.ActiveProtocol)
        {
            CLA = 0xFF,
            INS = 0xB0,
            P1 = 0x00,
            P2 = page,
            Le = 0x04
        };

        var response = TransmitApdu(reader, apdu);

        if (response.SW1 == 0x90 && response.SW2 == 0x00)
            return response.GetData();

        Console.WriteLine("Failed to read page {0}, SW1={1:X2}, SW2={2:X2}", page, response.SW1, response.SW2);
        return null;
    }
}
