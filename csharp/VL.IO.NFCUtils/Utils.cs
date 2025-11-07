// For examples, see:
// https://thegraybook.vvvv.org/reference/extending/writing-nodes.html#examples

using System.Formats.Asn1;
using PCSC;
using PCSC.Iso7816;
using NdefLibrary.Ndef;

namespace IO.NFC;

public static class Utils
{
    public static NdefMessage CreateNDEFMessage()
    {
        // 1. NDEF-Nachricht erstellen
        var textRecord = new NdefTextRecord
        {
            Text = "Hallo NTAG215!",
            LanguageCode = "de"
        };
        var message = new NdefMessage { textRecord };
        return message;
    }


    public static IsoReader CreateReader()
    {
        // 2. Verbindung zum NFC-Leser herstellen
        var contextFactory = ContextFactory.Instance;
        var context = contextFactory.Establish(SCardScope.System); // TODO: this should get disposed at some point
        var readerNames = context.GetReaders();
        var readerName = readerNames.FirstOrDefault();
        var isoReader = new IsoReader(context, readerName, SCardShareMode.Shared, SCardProtocol.Any, false);
        return isoReader;
    }

    public static void SendNDEFMessage(NdefMessage message, IsoReader isoReader)
    {
        // 3. APDU-Befehle zum Schreiben senden (vereinfacht, blockweise)
        byte[] ndefBytes = message.ToByteArray();
        for (int i = 0; i < ndefBytes.Length; i += 4)
        {
            byte[] block = ndefBytes.Skip(i).Take(4).ToArray();
            while (block.Length < 4) block = block.Append((byte)0x00).ToArray(); // Padding

            byte blockNumber = (byte)(4 + i / 4); // NTAG215 beginnt bei Page 4
            var apdu = new CommandApdu(IsoCase.Case4Short, isoReader.ActiveProtocol)
            {
                CLA = 0xFF,
                INS = 0xD6,
                P1 = 0x00,
                P2 = blockNumber,
                Data = block
            };

            var response = isoReader.Transmit(apdu);
            if (response.SW1 != 0x90)
            {
                Console.WriteLine($"Fehler beim Schreiben von Block {blockNumber}: SW={response.SW1:X2}{response.SW2:X2}");
                break;
            }
        }
    }
}