# Send to Bibliotaph (browser extension)

A Chrome and Edge extension (Manifest V3) for [Bibliotaph on the web](https://bibliotaph.online). Clicked on a D&D
Beyond, Demiplane, Humble Bundle or DriveThruRPG library page, it sends the page as it shows to Bibliotaph's
`/online/receive`, then fetches the covers Bibliotaph asks for. It does what the Send to Bibliotaph bookmark in
Settings › Online libraries does, without depending on the service's site letting a page it opens talk back, and it
can fetch covers kept on the services' image hosts. How the two talk to the site is in
[docs/architecture.md](../../docs/architecture.md#online-libraries).

- `background.js`: the toolbar button, the page snapshot (the Windows app's in-app browser takes the same one), and the
  cover fetches.
- `bibliotaph.js`: runs on bibliotaph.online. It marks the page so Settings can say the extension is installed, and on
  `/online/receive` hands over the page and the covers with the same messages the bookmark sends.
- Permissions: `activeTab` and `scripting` to read the page the button is clicked on; host access to bibliotaph.online
  and to the services' sites and image hosts, for the covers. Nothing runs until the button is clicked.

## Trying it

In Chrome open `chrome://extensions` (in Edge, `edge://extensions`), switch on **Developer mode**, choose
**Load unpacked** and pick this folder.

## Publishing

Raise `version` in `manifest.json` for each new upload, then make the package from the repository root:

```powershell
Compress-Archive -Path src\Bibliotaph.Extension\* -DestinationPath send-to-bibliotaph.zip -Force
```

- **Chrome Web Store:** in the [developer dashboard](https://chrome.google.com/webstore/devconsole) (a one-time
  registration fee), add a new item and upload the ZIP.
  - Single purpose: "Sends the user's online library page, and its covers, to their Bibliotaph library when they
    click the button."
  - Permission reasons: `activeTab`/`scripting` read the library page the button is clicked on; the hosts are
    Bibliotaph and the services whose covers it fetches.
  - Data use: website content, sent only to the user's own Bibliotaph account.
  - Privacy policy: https://bibliotaph.online/privacy
- **Edge Add-ons:** in [Partner Center](https://partner.microsoft.com/dashboard/microsoftedge/overview) (free), submit
  the same ZIP with the same answers.

Once it is listed, link the store page from the web guide.
