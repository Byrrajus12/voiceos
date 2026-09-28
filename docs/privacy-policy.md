# VOS Companion Privacy Policy

Effective date: September 27, 2026

VOS Companion is the Chrome integration for the VOS desktop app. It processes relevant tab/page information and webpage content to perform user-requested browser tasks, such as navigating websites, selecting controls, and entering text.

## Information processed

VOS Companion can provide the desktop app with open-tab metadata, including tab identifiers, URLs, titles, active status, and VOS task association. For a task page, it observes bounded visible page text and visible controls, including their labels, roles, ordinary form values, links, surrounding context, and geometry. It also processes browser task commands and navigation/action outcomes.

Ordinary page content and form values can contain personal or sensitive information, including names, email addresses, telephone numbers, login identifiers, and payment information. The extension does not use a general detector to remove all such information. Password-input values and values explicitly marked with the autocomplete tokens current-password, new-password, one-time-code, or cc-csc are omitted from page observations. Literal occurrences of those known values in observation text are also redacted. This protection does not identify every secret that a website or a user might place in ordinary text, URLs, or task instructions.

The extension does not request Chrome cookies, saved passwords, or the Chrome history database. Relevant URLs, tab metadata, and navigation information are nevertheless processed for browser tasks.

## Local communication and remote inference

VOS Companion communicates locally with VOS using Chrome Native Messaging. A native host bridges messages to the desktop app through a same-computer named pipe.

The desktop app may send information necessary for browser interpretation and planning to configured remote inference providers. The current integration uses TypeSafe for browser decisions and completion assessment, including page evidence, offered controls, task goals, and recent task actions. It also uses tab titles and origins for contextual or named-tab selection. When configured, OpenRouter receives task instructions for goal interpretation and selected-field/page context for text-value inference. Remote transmission can therefore include webpage content, ordinary form values, URLs, titles, and recent browser-task information.

Information does not all stay local, and page content may be transmitted remotely. Remote providers process information according to their own applicable terms and policies. This policy makes no guarantee about provider retention or use beyond the VOS functionality described here.

## Use and sharing

Browsing and page data are used to provide user-requested VOS browser functionality. They are not sold or used by VOS Companion for advertising or unrelated profiling. Transfers to inference providers support that functionality. VOS Companion's use of information received from Google APIs will adhere to the Chrome Web Store User Data Policy, including its Limited Use requirements.

The extension has no analytics service or remote telemetry reporting. Local diagnostic output records connection events, task/tab identifiers, navigation origins, action kinds, and timing/status information. The desktop app also produces diagnostics, which can include task instructions and tab/page metadata; these logs should be treated as potentially sensitive.

## Local state and user control

The extension stores limited state in Chrome session storage: task-tab/session associations, identifiers of adopted user tabs, and task-use sequence information. It does not persist page observations or form values in extension storage. Observation references and DOM mappings are kept in memory. Chrome session storage is temporary and is cleared when the browser restarts or the extension is disabled, reloaded, or updated.

The desktop app separately holds task observations and recent action history while performing browser tasks and may keep local diagnostics according to its configuration. This policy does not promise automatic deletion of desktop logs or remotely processed data.

Users can disable or uninstall VOS Companion to stop extension access. Users can also stop active VOS browser tasks through the desktop application. Users can manage the desktop app and its inference-provider configuration separately.

## Contact

Privacy questions: saipramodh12@gmail.com.
