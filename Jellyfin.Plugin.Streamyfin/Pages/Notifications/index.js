const saveBtn = document.getElementById('save-notification-btn');
const libraryContainer = document.getElementById('item-library-container');
const hiddenLibraryInput = document.getElementById('hidden-library-input');
const seerrUrlInput = document.getElementById('seerr-url-input');
const seerrApiKeyInput = document.getElementById('seerr-apikey-input');
const seerrJellyfinUrlInput = document.getElementById('seerr-jellyfin-url-input');
const seerrConnectBtn = document.getElementById('seerr-connect-btn');
const seerrTestBtn = document.getElementById('seerr-test-btn');
const seerrStatus = document.getElementById('seerr-status');

const showSeerrStatus = (message, isError = false) => {
    seerrStatus.style.display = 'block';
    seerrStatus.style.color = isError ? '#e53935' : '#43a047';
    seerrStatus.textContent = message;
};

const getValues = () => ({
    notifications: Array.from(document.querySelectorAll('[data-key-name][data-prop-name]')).reduce((acc, el) => {
        if (el.offsetParent === null) return acc;
        
        const notification = el.getAttribute('data-key-name');
        const property = el.getAttribute('data-prop-name');
        
        
        console.log("Notification", notification, el.offsetParent)

        const value = window.Streamyfin.shared.getElValue(el);
        acc[notification] = acc[notification] ?? {}

        if (value != null) {
            acc[notification][property] = value;
        }
        else delete acc[notification]

        return acc
    }, {})
})

// region helpers
const updateNotificationConfig = (name, config, valueName, value) => ({
    ...(config ?? {}),
    notifications: {
        ...(config?.notifications ?? {}),
        [name]: {
            ...(config?.notifications?.[name] ?? {}),
            [valueName]: value,
        }
    }
})
// endregion helpers

export default function (view, params) {

    // init code here
    view.addEventListener('viewshow', (e) => {
        import(window.ApiClient.getUrl("web/configurationpage?name=shared.js")).then(async (shared) => {
            shared.setPage("Notifications");
            
            document.getElementById("notification-endpoint").innerText = shared.NOTIFICATION_URL

            shared.setDomValues(document, shared.getConfig()?.notifications)
            shared.setOnConfigUpdatedListener('notifications', (config) => {
                console.log("updating dom for notifications")

                const {notifications} = config;
                shared.setDomValues(document, notifications);
            })

            const folders = await window.ApiClient.get("/Library/VirtualFolders")
                .then((response) => response.json())

            if (folders.length === 0) {
                libraryContainer.append("No libraries available")
            }

            folders.forEach(folder => {
                if (!document.getElementById(folder.ItemId)) {
                    const checkboxContainer = document.createElement("label")
                    const checkboxInput = document.createElement("input")
                    const checkboxLabel = document.createElement("span")

                    checkboxContainer.className = "emby-checkbox-label"

                    checkboxInput.setAttribute("id", folder.ItemId)
                    checkboxInput.setAttribute("type", "checkbox")
                    checkboxInput.setAttribute("is", "emby-checkbox")
                    
                    const libraries = shared.getConfig()?.notifications?.['itemAdded']?.['enabledLibraries'] ?? []
                    checkboxInput.checked = libraries.includes(folder.ItemId) === true

                    shared.keyedEventListener(checkboxInput, 'change', function () {
                        const isEnabled = checkboxInput.checked
                        let currentList = hiddenLibraryInput.value.split(",").filter(Boolean)

                        if (isEnabled)
                            currentList = [...new Set(currentList.concat(folder.ItemId))]
                        else
                            currentList = currentList.filter(id => id !== folder.ItemId)

                        hiddenLibraryInput.value = currentList.join(",")

                        shared.setConfig(updateNotificationConfig(
                            "itemAdded",
                            shared.getConfig(),
                            "enabledLibraries",
                            shared.getElValue(hiddenLibraryInput)
                        ));
                    })

                    checkboxLabel.className = "checkboxLabel"
                    checkboxLabel.innerText = folder.Name

                    checkboxContainer.append(
                        checkboxInput,
                        checkboxLabel
                    )

                    libraryContainer.append(checkboxContainer)
                }
            })

            document.querySelectorAll('[data-key-name][data-prop-name]').forEach(el => {
                shared.keyedEventListener(el, 'change', function () {
                    shared.setConfig(updateNotificationConfig(
                        el.getAttribute('data-key-name'),
                        shared.getConfig(),
                        el.getAttribute('data-prop-name'),
                        shared.getElValue(el)
                    ));
                })
            })

            shared.keyedEventListener(saveBtn, 'click', function (e) {
                e.preventDefault();
                shared.saveConfig()
            })

            const SEERR_STATUS_URL = window.ApiClient.getUrl('streamyfin/seerr/status');
            const SEERR_CONFIGURE_URL = window.ApiClient.getUrl('streamyfin/seerr/configure');
            const SEERR_TEST_URL = window.ApiClient.getUrl('streamyfin/seerr/test');

            try {
                const statusRes = await window.ApiClient.ajax({
                    type: 'GET', url: SEERR_STATUS_URL, contentType: 'application/json'
                });
                const status = await statusRes.json();
                if (status.connected) {
                    if (status.seerrUrl) seerrUrlInput.value = status.seerrUrl;
                    showSeerrStatus(`Connected (Webhook ID: ${status.webhookId ?? 'unknown'})`);
                    seerrTestBtn.style.display = '';
                }
            } catch (e) {
                console.warn('Failed to load Seerr status', e);
            }

            shared.keyedEventListener(seerrConnectBtn, 'click', async function (e) {
                e.preventDefault();
                const url = seerrUrlInput.value?.trim();
                const apiKey = seerrApiKeyInput.value?.trim();
                if (!url || !apiKey) {
                    showSeerrStatus('Please enter a Seerr URL and API key.', true);
                    return;
                }
                Dashboard.showLoadingMsg();
                try {
                    const res = await window.ApiClient.ajax({
                        type: 'POST',
                        url: SEERR_CONFIGURE_URL,
                        data: JSON.stringify({
                            SeerrUrl: url,
                            SeerrApiKey: apiKey,
                            JellyfinPublicUrl: seerrJellyfinUrlInput.value?.trim() || null
                        }),
                        contentType: 'application/json'
                    });
                    const result = await res.json();
                    if (result.success) {
                        showSeerrStatus(`Webhook configured! ID: ${result.webhookId ?? 'N/A'}`);
                        seerrTestBtn.style.display = '';
                    } else {
                        showSeerrStatus(`Failed: ${result.message ?? 'Unknown error'}`, true);
                    }
                } catch (err) {
                    showSeerrStatus(`Error: ${err.message ?? err}`, true);
                } finally {
                    Dashboard.hideLoadingMsg();
                }
            });

            shared.keyedEventListener(seerrTestBtn, 'click', async function (e) {
                e.preventDefault();
                Dashboard.showLoadingMsg();
                try {
                    const res = await window.ApiClient.ajax({
                        type: 'POST', url: SEERR_TEST_URL, contentType: 'application/json'
                    });
                    const result = await res.json();
                    result.success
                        ? showSeerrStatus('Test notification sent successfully!')
                        : showSeerrStatus(`Test failed: ${result.message ?? 'Unknown error'}`, true);
                } catch (err) {
                    showSeerrStatus(`Error: ${err.message ?? err}`, true);
                } finally {
                    Dashboard.hideLoadingMsg();
                }
            });
        })
    });
}