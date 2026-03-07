define(['connectionManager', 'globalize', 'loading', 'toast', 'confirm'], function (connectionManager, globalize, loading, toast, confirm) {
    const commandSourceState = {
        registered: false
    };

    function getSupportedItems(options) {
        const items = (options && options.items) ? options.items.filter(item => !!item) : [];
        if (!items.length) {
            return [];
        }

        const user = options && options.user;
        const users = options && options.users ? Object.values(options.users) : [];
        const firstUser = users.length ? users[0] : null;
        const hasAdminInfo = (user && user.Policy) || (firstUser && firstUser.Policy);
        const isAdmin = (user && user.Policy && user.Policy.IsAdministrator) ||
            (firstUser && firstUser.Policy && firstUser.Policy.IsAdministrator);

        if (hasAdminInfo && !isAdmin) {
            return [];
        }

        const supportedTypes = { Movie: true, Episode: true, Season: true, Series: true, Video: true };
        if (!items.every(item => supportedTypes[item.Type])) {
            return [];
        }

        return items;
    }

    function getCommandName() {
        const locale = (globalize.getCurrentLocale() || '').toLowerCase();
        return locale === 'zh-cn'
            ? '提取媒体信息'
            : (['zh-hk', 'zh-tw'].includes(locale) ? '提取媒體信息' : 'Extract MediaInfo');
    }

    function getDeleteCommandName() {
        const locale = (globalize.getCurrentLocale() || '').toLowerCase();
        return locale === 'zh-cn'
            ? '删除媒体信息'
            : (['zh-hk', 'zh-tw'].includes(locale) ? '刪除媒體信息' : 'Delete MediaInfo');
    }

    function getScanIntroCommandName() {
        const locale = (globalize.getCurrentLocale() || '').toLowerCase();
        return locale === 'zh-cn'
            ? '扫描片头'
            : (['zh-hk', 'zh-tw'].includes(locale) ? '掃描片頭' : 'Scan Intro');
    }

    function getSetIntroCommandName() {
        const locale = (globalize.getCurrentLocale() || '').toLowerCase();
        return locale === 'zh-cn'
            ? '设置片头'
            : (['zh-hk', 'zh-tw'].includes(locale) ? '設置片頭' : 'Set Intro');
    }

    function getClearIntroCommandName() {
        const locale = (globalize.getCurrentLocale() || '').toLowerCase();
        return locale === 'zh-cn'
            ? '清除片头'
            : (['zh-hk', 'zh-tw'].includes(locale) ? '清除片頭' : 'Clear Intro');
    }

    function getResultMessage(result, action) {
        const normalized = normalizeResult(result);
        const isDelete = action === 'delete';
        const isScanIntro = action === 'scan_intro';
        const isSetIntro = action === 'set_intro';
        const isClearIntro = action === 'clear_intro';
        if (!result) {
            return (isDelete ? getDeleteCommandName() : (isScanIntro ? getScanIntroCommandName() : (isSetIntro ? getSetIntroCommandName() : (isClearIntro ? getClearIntroCommandName() : getCommandName())))) + ' finished';
        }

        const locale = (globalize.getCurrentLocale() || '').toLowerCase();
        if (!normalized.hasStats) {
            if (locale === 'zh-cn') {
                return (isDelete ? '删除完成' : (isScanIntro ? '扫描完成' : (isSetIntro ? '设置完成' : (isClearIntro ? '清除完成' : '提取完成')))) + '（返回体无统计字段，请看日志）';
            }
            if (['zh-hk', 'zh-tw'].includes(locale)) {
                return (isDelete ? '刪除完成' : (isScanIntro ? '掃描完成' : (isSetIntro ? '設置完成' : (isClearIntro ? '清除完成' : '提取完成')))) + '（返回體無統計字段，請看日誌）';
            }
            return 'Completed (no stats in response, check server logs)';
        }

        if (locale === 'zh-cn') {
            const prefix = isDelete ? '删除完成' : (isScanIntro ? '扫描完成' : (isSetIntro ? '设置完成' : (isClearIntro ? '清除完成' : '提取完成')));
            return prefix + `：成功 ${normalized.succeeded}，失败 ${normalized.failed}，跳过 ${normalized.skipped}`;
        }

        if (['zh-hk', 'zh-tw'].includes(locale)) {
            const prefix = isDelete ? '刪除完成' : (isScanIntro ? '掃描完成' : (isSetIntro ? '設置完成' : (isClearIntro ? '清除完成' : '提取完成')));
            return prefix + `：成功 ${normalized.succeeded}，失敗 ${normalized.failed}，跳過 ${normalized.skipped}`;
        }

        return `Completed: success ${normalized.succeeded}, failed ${normalized.failed}, skipped ${normalized.skipped}`;
    }

    function tryParseJson(value) {
        if (typeof value !== 'string') {
            return null;
        }
        const normalized = value.replace(/^\uFEFF/, '').trim();
        if (!normalized) {
            return null;
        }
        try {
            return JSON.parse(normalized);
        } catch (_) {
            return null;
        }
    }

    function extractPayload(result) {
        if (result == null) {
            return {};
        }

        if (Array.isArray(result)) {
            if (!result.length) {
                return {};
            }
            for (const item of result) {
                const extractedItem = extractPayload(item);
                if (extractedItem && (
                    extractedItem.Succeeded != null || extractedItem.succeeded != null ||
                    extractedItem.Failed != null || extractedItem.failed != null ||
                    extractedItem.Skipped != null || extractedItem.skipped != null)) {
                    return extractedItem;
                }
            }
            return extractPayload(result[0]);
        }

        if (typeof result === 'string') {
            return tryParseJson(result) || {};
        }

        if (typeof result !== 'object') {
            return {};
        }

        if (result.Succeeded != null || result.succeeded != null ||
            result.Failed != null || result.failed != null ||
            result.Skipped != null || result.skipped != null) {
            return result;
        }

        const nestedCandidates = [
            result.responseJSON,
            result.data,
            result.response,
            result.result,
            result.body,
            result.content,
            result.Content,
            result.value
        ];

        for (const candidate of nestedCandidates) {
            if (candidate == null) {
                continue;
            }
            const extracted = extractPayload(candidate);
            if (extracted && (
                extracted.Succeeded != null || extracted.succeeded != null ||
                extracted.Failed != null || extracted.failed != null ||
                extracted.Skipped != null || extracted.skipped != null)) {
                return extracted;
            }
        }

        const textCandidates = [result.responseText, result.text, result.Text];
        for (const text of textCandidates) {
            const parsed = tryParseJson(text);
            if (parsed) {
                return extractPayload(parsed);
            }
        }

        return result;
    }

    function normalizeResult(result) {
        const payload = extractPayload(result);
        const succeededRaw = payload.Succeeded ?? payload.succeeded ?? payload.Success ?? payload.success ?? 0;
        const failedRaw = payload.Failed ?? payload.failed ?? payload.Error ?? payload.error ?? 0;
        const skippedRaw = payload.Skipped ?? payload.skipped ?? 0;
        const hasStats =
            payload.Succeeded != null || payload.succeeded != null ||
            payload.Success != null || payload.success != null ||
            payload.Failed != null || payload.failed != null ||
            payload.Skipped != null || payload.skipped != null;
        return {
            succeeded: Number.isFinite(Number(succeededRaw)) ? Number(succeededRaw) : 0,
            failed: Number.isFinite(Number(failedRaw)) ? Number(failedRaw) : 0,
            skipped: Number.isFinite(Number(skippedRaw)) ? Number(skippedRaw) : 0,
            hasStats: hasStats
        };
    }

    function getErrorMessage(action, err) {
        const isDelete = action === 'delete';
        const isScanIntro = action === 'scan_intro';
        const isSetIntro = action === 'set_intro';
        const isClearIntro = action === 'clear_intro';
        const commandName = isDelete ? getDeleteCommandName() : (isScanIntro ? getScanIntroCommandName() : (isSetIntro ? getSetIntroCommandName() : (isClearIntro ? getClearIntroCommandName() : getCommandName())));
        const detail = (err && (err.message || err.statusText || err.responseText)) ? ` (${err.message || err.statusText || err.responseText})` : '';
        return commandName + ' failed' + detail;
    }

    function postJson(apiClient, endpoint, body) {
        const url = apiClient.getUrl(endpoint);
        return apiClient.ajax({
            type: 'POST',
            url: url,
            data: JSON.stringify(body || {}),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (result) {
            return result || {};
        });
    }

    function ticksToTimeString(ticks) {
        const ticksNumber = Number(ticks);
        if (!Number.isFinite(ticksNumber) || ticksNumber < 0) {
            return null;
        }

        const totalMilliseconds = Math.floor(ticksNumber / 10000);
        const hours = Math.floor(totalMilliseconds / 3600000);
        const minutes = Math.floor((totalMilliseconds % 3600000) / 60000);
        const seconds = Math.floor((totalMilliseconds % 60000) / 1000);
        const milliseconds = totalMilliseconds % 1000;

        const hh = String(hours).padStart(2, '0');
        const mm = String(minutes).padStart(2, '0');
        const ss = String(seconds).padStart(2, '0');
        const mmm = String(milliseconds).padStart(3, '0');
        return `${hh}:${mm}:${ss}.${mmm}`;
    }

    function getIntroTicksFromItem(item) {
        if (!item || !Array.isArray(item.Chapters)) {
            return null;
        }

        let introStartTicks = null;
        let introEndTicks = null;

        for (const chapter of item.Chapters) {
            if (!chapter || chapter.StartPositionTicks == null) {
                continue;
            }

            if (chapter.MarkerType === 'IntroStart' || chapter.MarkerType === 7) {
                introStartTicks = chapter.StartPositionTicks;
            } else if (chapter.MarkerType === 'IntroEnd' || chapter.MarkerType === 8) {
                introEndTicks = chapter.StartPositionTicks;
            }
        }

        if (introStartTicks == null && introEndTicks == null) {
            return null;
        }

        return { introStartTicks, introEndTicks };
    }

    function getExistingIntroTicks(apiClient, episodeItem) {
        const fromCurrentItem = getIntroTicksFromItem(episodeItem);
        if (fromCurrentItem) {
            return Promise.resolve(fromCurrentItem);
        }

        if (!episodeItem || !episodeItem.Id) {
            return Promise.resolve(null);
        }

        const userId = typeof apiClient.getCurrentUserId === 'function' ? apiClient.getCurrentUserId() : null;
        const endpoint = userId ? `Users/${userId}/Items/${episodeItem.Id}` : `Items/${episodeItem.Id}`;
        const query = new URLSearchParams({ Fields: 'Chapters' }).toString();
        const url = `${apiClient.getUrl(endpoint)}?${query}`;

        return apiClient.ajax({
            type: 'GET',
            url: url,
            dataType: 'json'
        }).then(function (item) {
            return getIntroTicksFromItem(item);
        }).catch(function () {
            return null;
        });
    }

    const api = {
        extractMediaInfo: function (ids) {
            if (!ids || !ids.length) {
                return Promise.resolve();
            }

            const commandName = getCommandName();
            return confirm({
                text: globalize.translate('AreYouSureToContinue'),
                title: commandName,
                confirmText: commandName,
                primary: 'cancel'
            }).then(function () {
                loading.show();
                const apiClient = connectionManager.currentApiClient();
                return postJson(apiClient, 'MediaInfoKeeper/Items/ExtractMediaInfo', { Ids: ids }).then(function (result) {
                    toast(getResultMessage(result, 'extract'));
                }).catch(function (err) {
                    toast(getErrorMessage('extract', err));
                }).finally(function () {
                    loading.hide();
                });
            });
        },

        deleteMediaInfoPersist: function (ids) {
            if (!ids || !ids.length) {
                return Promise.resolve();
            }

            const commandName = getDeleteCommandName();
            return confirm({
                text: globalize.translate('AreYouSureToContinue'),
                title: commandName,
                confirmText: globalize.translate('Delete'),
                primary: 'cancel'
            }).then(function () {
                loading.show();
                const apiClient = connectionManager.currentApiClient();
                return postJson(apiClient, 'MediaInfoKeeper/Items/DeleteMediaInfoPersist', { Ids: ids }).then(function (result) {
                    toast(getResultMessage(result, 'delete'));
                }).catch(function (err) {
                    toast(getErrorMessage('delete', err));
                }).finally(function () {
                    loading.hide();
                });
            });
        },

        scanIntro: function (ids) {
            if (!ids || !ids.length) {
                return Promise.resolve();
            }

            const commandName = getScanIntroCommandName();
            return confirm({
                text: globalize.translate('AreYouSureToContinue'),
                title: commandName,
                confirmText: commandName,
                primary: 'cancel'
            }).then(function () {
                loading.show();
                const apiClient = connectionManager.currentApiClient();
                return postJson(apiClient, 'MediaInfoKeeper/Items/ScanIntro', { Ids: ids }).then(function (result) {
                    toast(getResultMessage(result, 'scan_intro'));
                }).catch(function (err) {
                    toast(getErrorMessage('scan_intro', err));
                }).finally(function () {
                    loading.hide();
                });
            });
        },

        setIntro: function (ids, items) {
            if (!ids || !ids.length) {
                return Promise.resolve();
            }

            const commandName = getSetIntroCommandName();
            const locale = (globalize.getCurrentLocale() || '').toLowerCase();
            const selectedItems = Array.isArray(items) ? items.filter(Boolean) : [];
            const defaultTimeValue = '00:00:00.000';
            
            function timeToSeconds(timeStr) {
                const parts = timeStr.split(':');
                if (parts.length === 3) {
                    const hours = parseFloat(parts[0]) || 0;
                    const minutes = parseFloat(parts[1]) || 0;
                    const seconds = parseFloat(parts[2]) || 0;
                    return hours * 3600 + minutes * 60 + seconds;
                }
                return 0;
            }
            
            return new Promise(function (resolve) {
                const dialogHtml = `
                    <div class="dialogContainer" style="position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: rgba(0,0,0,0.7); display: flex; align-items: center; justify-content: center; z-index: 9999;">
                        <div class="formDialogContent smoothScrollY" style="background: #101010; border-radius: 8px; padding: 24px; max-width: 90%; width: 500px; max-height: 90vh; overflow-y: auto;">
                            <h3 style="margin: 0 0 20px 0; color: #fff; font-size: 1.5em;">${locale === 'zh-cn' ? '设置片头时间' : (['zh-hk', 'zh-tw'].includes(locale) ? '設置片頭時間' : 'Set Intro Time')}</h3>
                            <div class="inputContainer" style="margin-bottom: 16px;">
                                <label style="display: block; margin-bottom: 8px; color: #fff; font-size: 0.9em;">${locale === 'zh-cn' ? '片头开始时间' : (['zh-hk', 'zh-tw'].includes(locale) ? '片頭開始時間' : 'Intro Start Time')}</label>
                                <input type="text" id="introStartTime" class="emby-input" value="${defaultTimeValue}" placeholder="${defaultTimeValue}" style="width: 100%; padding: 10px; background: #1f1f1f; border: 1px solid #333; color: #fff; border-radius: 4px; font-size: 16px; box-sizing: border-box;">
                            </div>
                            <div class="inputContainer" style="margin-bottom: 16px;">
                                <label style="display: block; margin-bottom: 8px; color: #fff; font-size: 0.9em;">${locale === 'zh-cn' ? '片头结束时间' : (['zh-hk', 'zh-tw'].includes(locale) ? '片頭結束時間' : 'Intro End Time')}</label>
                                <input type="text" id="introEndTime" class="emby-input" value="${defaultTimeValue}" placeholder="${defaultTimeValue}" style="width: 100%; padding: 10px; background: #1f1f1f; border: 1px solid #333; color: #fff; border-radius: 4px; font-size: 16px; box-sizing: border-box;">
                            </div>
                            <div style="margin-top: 24px; display: flex; gap: 10px; flex-wrap: wrap;">
                                <button id="cancelSetIntro" class="emby-button emby-button-cancel" style="flex: 1; min-width: 100px; padding: 12px 20px; background: #333; color: #fff; border: none; border-radius: 4px; cursor: pointer; font-size: 14px; display: flex; justify-content: center; align-items: center;">${globalize.translate('Cancel')}</button>
                                <button id="confirmSetIntro" class="emby-button emby-button-submit" style="flex: 1; min-width: 100px; padding: 12px 20px; background: #53B54C; color: #fff; border: none; border-radius: 4px; cursor: pointer; font-size: 14px; display: flex; justify-content: center; align-items: center;">${commandName}</button>
                            </div>
                        </div>
                    </div>
                `;

                const dialog = document.createElement('div');
                dialog.innerHTML = dialogHtml;
                document.body.appendChild(dialog);

                const cancelBtn = dialog.querySelector('#cancelSetIntro');
                const confirmBtn = dialog.querySelector('#confirmSetIntro');
                const startInput = dialog.querySelector('#introStartTime');
                const endInput = dialog.querySelector('#introEndTime');
                const shouldPrefill = selectedItems.length === 1 && selectedItems[0].Type === 'Episode';

                if (shouldPrefill) {
                    const apiClient = connectionManager.currentApiClient();
                    getExistingIntroTicks(apiClient, selectedItems[0]).then(function (introTicks) {
                        if (!introTicks) {
                            return;
                        }

                        if (introTicks.introStartTicks != null && startInput.value === defaultTimeValue) {
                            const formattedStart = ticksToTimeString(introTicks.introStartTicks);
                            if (formattedStart) {
                                startInput.value = formattedStart;
                            }
                        }

                        if (introTicks.introEndTicks != null && endInput.value === defaultTimeValue) {
                            const formattedEnd = ticksToTimeString(introTicks.introEndTicks);
                            if (formattedEnd) {
                                endInput.value = formattedEnd;
                            }
                        }
                    });
                }

                cancelBtn.addEventListener('click', function () {
                    document.body.removeChild(dialog);
                    resolve();
                });

                confirmBtn.addEventListener('click', function () {
                    const startSeconds = timeToSeconds(startInput.value);
                    const endSeconds = timeToSeconds(endInput.value);

                    if (startSeconds >= endSeconds) {
                        toast(locale === 'zh-cn' ? '开始时间必须小于结束时间' : (['zh-hk', 'zh-tw'].includes(locale) ? '開始時間必須小於結束時間' : 'Start time must be less than end time'));
                        return;
                    }

                    const introStartTicks = Math.round(startSeconds * 10000000);
                    const introEndTicks = Math.round(endSeconds * 10000000);

                    document.body.removeChild(dialog);
                    loading.show();
                    const apiClient = connectionManager.currentApiClient();
                    return postJson(apiClient, 'MediaInfoKeeper/Items/SetIntro', { 
                        Ids: ids, 
                        IntroStartTicks: introStartTicks, 
                        IntroEndTicks: introEndTicks 
                    }).then(function (result) {
                        toast(getResultMessage(result, 'set_intro'));
                        resolve();
                    }).catch(function (err) {
                        toast(getErrorMessage('set_intro', err));
                        resolve();
                    }).finally(function () {
                        loading.hide();
                    });
                });
            });
        },

        clearIntro: function (ids) {
            if (!ids || !ids.length) {
                return Promise.resolve();
            }

            const commandName = getClearIntroCommandName();
            const locale = (globalize.getCurrentLocale() || '').toLowerCase();
            
            return new Promise(function (resolve) {
                const dialogHtml = `
                    <div class="dialogContainer" style="position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: rgba(0,0,0,0.7); display: flex; align-items: center; justify-content: center; z-index: 9999;">
                        <div class="formDialogContent smoothScrollY" style="background: #101010; border-radius: 8px; padding: 24px; max-width: 90%; width: 500px; max-height: 90vh; overflow-y: auto;">
                            <h3 style="margin: 0 0 20px 0; color: #fff; font-size: 1.5em;">${locale === 'zh-cn' ? '清除片头' : (['zh-hk', 'zh-tw'].includes(locale) ? '清除片頭' : 'Clear Intro')}</h3>
                            <p style="margin: 0 0 24px 0; color: #ccc; font-size: 14px;">${locale === 'zh-cn' ? '确定要清除选中项目的片头标记吗？' : (['zh-hk', 'zh-tw'].includes(locale) ? '確定要清除選中項目的片頭標記嗎？' : 'Are you sure you want to clear intro markers for selected items?')}</p>
                            <div style="margin-top: 24px; display: flex; gap: 10px; flex-wrap: wrap;">
                                <button id="cancelClearIntro" class="emby-button emby-button-cancel" style="flex: 1; min-width: 100px; padding: 12px 20px; background: #333; color: #fff; border: none; border-radius: 4px; cursor: pointer; font-size: 14px; display: flex; justify-content: center; align-items: center;">${globalize.translate('Cancel')}</button>
                                <button id="confirmClearIntro" class="emby-button emby-button-submit" style="flex: 1; min-width: 100px; padding: 12px 20px; background: #00a4dc; color: #fff; border: none; border-radius: 4px; cursor: pointer; font-size: 14px; display: flex; justify-content: center; align-items: center;">${commandName}</button>
                            </div>
                        </div>
                    </div>
                `;

                const dialog = document.createElement('div');
                dialog.innerHTML = dialogHtml;
                document.body.appendChild(dialog);

                const cancelBtn = dialog.querySelector('#cancelClearIntro');
                const confirmBtn = dialog.querySelector('#confirmClearIntro');

                cancelBtn.addEventListener('click', function () {
                    document.body.removeChild(dialog);
                    resolve();
                });

                confirmBtn.addEventListener('click', function () {
                    document.body.removeChild(dialog);
                    loading.show();
                    const apiClient = connectionManager.currentApiClient();
                    return postJson(apiClient, 'MediaInfoKeeper/Items/ClearIntro', { Ids: ids }).then(function (result) {
                        toast(getResultMessage(result, 'clear_intro'));
                        resolve();
                    }).catch(function (err) {
                        toast(getErrorMessage('clear_intro', err));
                        resolve();
                    }).finally(function () {
                        loading.hide();
                    });
                });
            });
        }
    };

    function buildCommandSource() {
        return {
            getCommands: function (options) {
                const items = getSupportedItems(options);
                if (!items.length) {
                    return [];
                }

                const commands = [];
                commands.push({ name: getCommandName(), id: 'extract_media_info', icon: '4k' });
                commands.push({ name: getDeleteCommandName(), id: 'delete_media_info_persist', icon: 'delete_forever' });

                const introSupportedTypes = { Episode: true, Season: true, Series: true };
                if (items.every(item => introSupportedTypes[item.Type])) {
                    commands.push({ name: getScanIntroCommandName(), id: 'scan_intro', icon: 'graphic_eq' });
                    commands.push({ name: getSetIntroCommandName(), id: 'set_intro', icon: 'schedule' });
                    commands.push({ name: getClearIntroCommandName(), id: 'clear_intro', icon: 'delete_forever' });
                }

                return commands;
            },
            executeCommand: function (command, items) {
                if (!items || !items.length) {
                    return;
                }

                const ids = items.map(item => item && item.Id).filter(Boolean);
                if (!ids.length) {
                    return;
                }

                if (command === 'extract_media_info') {
                    return api.extractMediaInfo(ids);
                }

                if (command === 'delete_media_info_persist') {
                    return api.deleteMediaInfoPersist(ids);
                }

                if (command === 'scan_intro') {
                    return api.scanIntro(ids);
                }

                if (command === 'set_intro') {
                    return api.setIntro(ids, items);
                }

                if (command === 'clear_intro') {
                    return api.clearIntro(ids);
                }
            }
        };
    }

    (function registerCommandSource(attempt) {
        const maxAttempts = 20;
        Emby.importModule('./modules/common/itemmanager/itemmanager.js')
            .then(itemmanager => {
                if (!itemmanager || typeof itemmanager.registerCommandSource !== 'function') {
                    throw new Error('itemmanager unavailable');
                }

                if (commandSourceState.registered) {
                    return;
                }

                itemmanager.registerCommandSource(buildCommandSource());
                commandSourceState.registered = true;
            })
            .catch(() => {
                if (attempt < maxAttempts) {
                    setTimeout(() => registerCommandSource(attempt + 1), 150);
                }
            });
    })(0);

    return api;
});
