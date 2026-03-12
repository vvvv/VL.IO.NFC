# VL.IO.NFC

Reads and writes data from and to NFC tags using a supported USB NFC reader (such as an ACR122U).  

- Writes any URL or String to an NFC tag 
- Reads the UID and Data of the tag
- Formats it as UID hex without spaces, e.g. 04A2B3C4D5E6F7
- Returns status for each write and read operation
- Tested with NFC Reader: ACS ACR1552U-MW
- Tested with NFC Tags: ISO 14443 Type A, ISO 14443 Type B, ISO 15693, ISO 18092, MIFARE, FeliCa, PC/SC, NFC Forum - Type 1, NFC Forum - Type 2, NFC Forum - Type 3, NFC Forum - Type 4, NFC Forum - Type 5

For use with vvvv, the visual live-programming environment for .NET: http://vvvv.org

## Getting started
- Install as [described here](https://thegraybook.vvvv.org/reference/hde/managing-nugets.html) via commandline:

    `nuget install VL.IO.NFC -pre`

- Usage examples and more information are included in the pack and can be found via the [Help Browser](https://thegraybook.vvvv.org/reference/hde/findinghelp.html)

## Contributing
- Report issues on [the vvvv forum](https://forum.vvvv.org/c/vvvv-gamma/28)
- For custom development requests, please [get in touch](mailto:devvvvs@vvvv.org)
- When making a pull-request, please make sure to read the general [guidelines on contributing to vvvv libraries](https://thegraybook.vvvv.org/reference/extending/contributing.html)

## Credits
Based on:
- https://www.nuget.org/packages/PCSC
- https://www.nuget.org/packages/PCSC.Iso7816
- https://www.nuget.org/packages/NdefLibrary

Developed by:  
- https://github.com/Delivers
- https://github.com/vvvv

## Sponsoring
Development of this library was partially sponsored by:
- [Refik Anadol Studio](https://refikanadolstudio.com/)
