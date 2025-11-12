# VL.IO.NFC

# 🪪 NFC URL Writer & Reader

A lightweight tool that writes a **URL** or **String** to an **NFC tags** using a supported USB NFC reader (such as an ACR122U).  
This version **does not require or set any password** on the tag — it simply writes the provided URL/Strng or reads when a tag is detected.

---

## ✨ Features

- 🔹 Writes any URL or String to an NFC tag 
- 🔹 Reads the UID and Data of the tag
- 🔹 Formats it as UID hex without spaces, e.g. 04A2B3C4D5E6F7
- 🔹 Prints detailed status for each write and read operation  

For use with vvvv, the visual live-programming environment for .NET: http://vvvv.org

## Getting started
- Install as [described here](https://thegraybook.vvvv.org/reference/hde/managing-nugets.html) via commandline:

    `nuget install VL.IO.NFC -pre`

- Usage examples and more information are included in the pack and can be found via the [Help Browser](https://thegraybook.vvvv.org/reference/hde/findinghelp.html)

- Once the VL.IO.NFC nuget is installed and referenced in your VL document you'll see the category "IO.NFC" in the nodebrowser. Press F1 to open the Help Browser and search for the term "nfc" to see relevant how-to patches.

## Contributing
- Report issues on [the vvvv forum](https://forum.vvvv.org/c/vvvv-gamma/28)
- For custom development requests, please [get in touch](mailto:devvvvs@vvvv.org)
- When making a pull-request, please make sure to read the general [guidelines on contributing to vvvv libraries](https://thegraybook.vvvv.org/reference/extending/contributing.html)

## Credits
Links to libraries this is based on:
* https://www.nuget.org/packages/PCSC
* https://www.nuget.org/packages/PCSC.Iso7816
* https://www.nuget.org/packages/NdefLibrary


## Sponsoring
Development of this library was partially sponsored by:  
* https://github.com/Delivers
* https://github.com/vvvv
