# Browser store listing draft

This file is the source text for Chrome Web Store and Microsoft Edge Add-ons review forms. The public project URLs below must be checked against each store's current submission requirements before submission; store publication has not been completed.

## Public project URLs

- Repository: https://github.com/axelose/PCActivityTracker
- Support: https://github.com/axelose/PCActivityTracker/issues
- Privacy policy: https://github.com/axelose/PCActivityTracker/blob/main/PRIVACY.md
- Private vulnerability reporting: https://github.com/axelose/PCActivityTracker/security

Do not attach real activity databases, browser tokens, URLs, or window titles to public support issues. Use synthetic examples or the application's anonymized diagnostic export.

## Name

PC Activity Tracker Browser Bridge

## Single purpose

Send only the currently active normal tab's site information to the PC Activity Tracker application running on the same Windows PC, so the user can include website time in a local activity timeline.

## Short description

Connects the active Chrome/Edge tab to the local-only PC Activity Tracker. No browsing data is sent to the developer or a cloud service.

## Permission justifications

- `tabs`: Read the active tab URL and title after the user installs and configures the extension. It does not enumerate or upload browser history.
- `storage`: Keep the user's local bridge token, pause state, and generic last local connection result in browser-local extension storage. The stored result contains no URL, domain, or page title, and storage access is restricted to trusted extension contexts.
- `alarms`: Send a low-frequency heartbeat while the same tab remains active so elapsed time can be closed accurately after a crash or missed event.
- `nativeMessaging`: Connect only to the installed `jp.local.pcactivitytracker.browser` host. Chromium validates the host manifest and extension ID; the host forwards framed messages through a Windows current-user-only named pipe. No network host permission is declared.
- YouTube content script: Read only the public channel name displayed on supported YouTube pages. It does not read page text generally, comments, form input, cookies, or credentials.

## Data-use declaration

Handled data: active-tab URL, page title, browser name, and—for supported YouTube/Twitch pages—the public channel or streamer name. The data is used only for the user-facing local activity-history feature, is not sold, is not used for advertising or credit decisions, and is not transferred to the developer or third parties.

Incognito/InPrivate access is disabled in the manifest. The extension has no network host permission and uses only the registered Native Messaging host; the desktop application removes URL user information, query strings, and fragments before encrypted persistence.

## Reviewer setup

1. Install and start the matching PC Activity Tracker desktop version.
2. Complete the desktop privacy onboarding and enable recording.
3. Copy the extension ID shown on the options page into Settings > Chrome / Edge integration and register it.
4. Restart the browser, copy the desktop bridge token into the extension options page, and choose “保存して接続確認”.
5. Open a normal HTTPS page, keep the browser foreground, and confirm the site appears in Today.
6. Use the popup to verify pause/resume. Reset pairing in the desktop app to verify the host registration and old token are revoked.

## Required submission assets and external steps

- Check that the public privacy-policy URL above is accessible and accepted by both stores. If a dedicated policy page is required, host the same policy there and update the listing URLs before submission.
- Use the public support URL above and verify that the private vulnerability-reporting entry point is available.
- Supply current screenshots showing the options page, popup, and desktop Today screen.
- Complete each store's data-use form consistently with the declarations above.
- Do not submit the unpacked development build or an unsigned desktop binary as a public release.
