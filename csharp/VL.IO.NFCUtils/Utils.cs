using System;
using System.Collections.Generic;
using NdefLibrary.Ndef;
using PCSC;
using PCSC.Iso7816;
using PCSC.Utils;

namespace IO.NFC;

public static class Utils
{
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
