// File drop zone JS interop for Blazor Server
// Handles native file drag-and-drop by uploading directly via fetch(),
// bypassing InputFile which has cross-browser issues with synthetic events.

import { LARGE_FOLDER_FILES, failureReport, fromFolderPicker, runFolderUpload, walkEntries } from './folderUpload.js';

// LAYER 1: Document-level prevention — stops the browser from EVER
// navigating to a dropped file, regardless of element state.
document.addEventListener('dragover', (e) => e.preventDefault());
document.addEventListener('drop', (e) => e.preventDefault());

// The folder input is recreated each time the Files tab returns, so its change event is caught here.
document.addEventListener('change', (e) => {
    if (e.target.id !== 'folderUploadInput' || !page || !panel) return;
    const items = fromFolderPicker(e.target.files);
    e.target.value = ''; // Reset so the same folder can be re-selected
    startFolderUpload(async () => ({ files: items, unreadable: [] }));
});

// A folder takes minutes, so its run belongs to this module, which outlives the page: moving around the app leaves it
// running, and coming back to its container shows it again. The page gets counts and a short failure list, because
// a call into Blazor is limited to 32 KB; the full list stays here for the download.
const PROGRESS_INTERVAL_MS = 500;
const FAILURES_SHOWN = 200;
const FAILURES_PER_CALL = 10;
const FAILURE_TEXT_SHOWN = 400;

let page = null; // The FileBrowser showing right now, as { dotNetRef, containerId, basePath }.
let panel = null; // Its FolderUploadRun, as { dotNetRef, containerId }.
let run = null; // The running or last finished folder upload.

const warnBeforeLeaving = (e) => e.preventDefault();

function tellPage(method, ...args) {
    if (!run || panel?.containerId !== run.containerId) return Promise.resolve();
    return panel.dotNetRef.invokeMethodAsync(method, ...args).catch(() => { /* The page has gone; its next visit catches up. */ });
}

const report = () => tellPage('UpdateFolderUpload', JSON.stringify(run.progress));

function reportSoon() {
    if (run.reportTimer !== null) return;
    run.reportTimer = setTimeout(() => {
        run.reportTimer = null;
        if (!run.finished) report();
    }, PROGRESS_INTERVAL_MS);
}

async function reportFailures() {
    const shown = run.failures.slice(0, FAILURES_SHOWN).map(({ path, error }) => ({
        path: path.slice(-FAILURE_TEXT_SHOWN),
        error: error.slice(0, FAILURE_TEXT_SHOWN),
    }));
    for (let i = 0; i < shown.length; i += FAILURES_PER_CALL) {
        await tellPage('AddFolderUploadFailures', JSON.stringify(shown.slice(i, i + FAILURES_PER_CALL)));
    }
}

// Runs one folder upload. collect finds the files; a drop has to have taken its entries before this is called,
// because the browser empties them once the drop event ends.
async function startFolderUpload(collect) {
    if (run && !run.finished) {
        panel.dotNetRef.invokeMethodAsync('ShowFolderUploadBusy').catch(() => { });
        return;
    }

    const { dotNetRef, containerId, basePath } = page;
    const abort = new AbortController();
    run = {
        containerId, abort, confirm: null, finished: false, reportTimer: null, failures: [],
        progress: { stage: 'reading', found: 0, total: 0, succeeded: 0, failed: 0, waiting: false, cancelled: false, stoppedBecause: null },
    };
    const thisRun = run;
    const { progress } = thisRun;
    window.addEventListener('beforeunload', warnBeforeLeaving);

    try {
        const currentPath = await dotNetRef.invokeMethodAsync('GetCurrentPath');
        const supportedExtensions = await panel.dotNetRef.invokeMethodAsync('GetSupportedExtensions');
        await report();

        const { files, unreadable } = await collect((found) => {
            progress.found = found;
            reportSoon();
        });
        progress.found = progress.total = files.length + unreadable.length;

        if (progress.total > LARGE_FOLDER_FILES && !abort.signal.aborted) {
            progress.stage = 'confirm';
            await report();
            if (!await new Promise((resolve) => { thisRun.confirm = resolve; })) abort.abort();
            thisRun.confirm = null;
        }

        if (!abort.signal.aborted) {
            progress.stage = 'uploading';
            await report();
        }
        const finished = await runFolderUpload(abort.signal.aborted ? [] : files, {
            url: `${basePath}/api/containers/${containerId}/files`,
            basePath: currentPath,
            unreadable: abort.signal.aborted ? [] : unreadable,
            supportedExtensions,
            signal: abort.signal,
            onProgress(state) {
                progress.succeeded = state.succeeded;
                progress.failed = state.failures.length;
                progress.waiting = state.waiting;
                reportSoon();
            },
        });
        thisRun.failures = finished.failures;
        progress.stoppedBecause = finished.stoppedBecause;
    } catch (err) {
        progress.stoppedBecause = `The upload stopped: ${err?.message ?? err}`;
    }

    progress.cancelled = abort.signal.aborted;
    progress.stage = 'finished';
    thisRun.finished = true;
    clearTimeout(thisRun.reportTimer);
    window.removeEventListener('beforeunload', warnBeforeLeaving);
    await report();
    await reportFailures();
}

/** Connects a panel to any run in its container, so a page opened during or after a run picks it up. */
export async function attachFolderUploadPanel(dotNetRef, containerId) {
    panel = { dotNetRef, containerId };
    if (!run || run.containerId !== containerId) return;
    await report();
    if (run.finished) await reportFailures();
}

export function cancelFolderUpload() {
    if (!run || run.finished) return;
    run.abort.abort();
    run.confirm?.(false);
}

export function confirmFolderUpload() {
    run?.confirm?.(true);
}

export function dismissFolderUpload() {
    if (run?.finished) run = null;
}

export function downloadFolderUploadFailures() {
    if (!run) return;
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([failureReport(run.failures)], { type: 'text/plain' }));
    link.download = 'folder-upload-failures.txt';
    link.click();
    URL.revokeObjectURL(link.href);
}

/** iOS shows the folder input but gives it no working picker, so the page leaves the button out there. */
export function supportsFolderPicker() {
    const isIos = /iPad|iPhone|iPod/.test(navigator.userAgent)
        || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1); // iPadOS calls itself a Mac
    return !isIos && 'webkitdirectory' in document.createElement('input');
}

export function initializeDropZone(dropZoneId, dotNetRef, containerId, basePath) {
    page = { dotNetRef, containerId, basePath: basePath || '' };

    let dragCounter = 0;
    let bound = false;

    async function uploadFiles(files) {
        if (files.length === 0) return;

        const currentPath = await dotNetRef.invokeMethodAsync('GetCurrentPath');

        const formData = new FormData();
        const placeholders = [];
        for (let i = 0; i < files.length; i++) {
            formData.append('files', files[i]);
            // Build the virtual path the API will assign so post-fetch dedup matches.
            // Mirrors the server's logic: currentPath ('/' or '/sub/') + file name.
            const path = (currentPath === '/' ? '/' : currentPath) + files[i].name;
            placeholders.push({ name: files[i].name, path: path, sizeBytes: files[i].size });
        }
        formData.append('path', currentPath);

        // Eager-UI: insert placeholder rows BEFORE the upload fetch so the user sees
        // immediate feedback. HandleDropUploadResult dedups by path when the real
        // DocumentId arrives back from the API.
        try {
            await dotNetRef.invokeMethodAsync('AddUploadPlaceholders', JSON.stringify(placeholders));
        } catch {
            // Non-fatal — the post-fetch insert still works if this fails.
        }

        try {
            const prefix = basePath || '';
            const response = await fetch(`${prefix}/api/containers/${containerId}/files`, {
                method: 'POST',
                body: formData
            });

            if (response.ok) {
                const result = await response.json();
                await dotNetRef.invokeMethodAsync('HandleDropUploadResult', JSON.stringify(result));
            } else {
                await dotNetRef.invokeMethodAsync('HandleDropUploadError',
                    `Upload failed (HTTP ${response.status})`);
            }
        } catch (err) {
            await dotNetRef.invokeMethodAsync('HandleDropUploadError', err.message);
        }
    }

    function bindEvents(element) {
        if (bound) return;
        bound = true;

        // Drag-and-drop events
        element.addEventListener('dragenter', (e) => {
            e.preventDefault();
            e.stopPropagation();
            dragCounter++;
            element.classList.add('dragging');
        });

        element.addEventListener('dragover', (e) => {
            e.preventDefault();
            e.stopPropagation();
        });

        element.addEventListener('dragleave', (e) => {
            e.preventDefault();
            e.stopPropagation();
            dragCounter--;
            if (dragCounter <= 0) {
                dragCounter = 0;
                element.classList.remove('dragging');
            }
        });

        element.addEventListener('drop', async (e) => {
            e.preventDefault();
            e.stopPropagation();
            dragCounter = 0;
            element.classList.remove('dragging');

            const entries = Array.from(e.dataTransfer.items ?? [])
                .filter((item) => item.kind === 'file')
                .map((item) => item.webkitGetAsEntry?.())
                .filter(Boolean);
            if (entries.some((entry) => entry.isDirectory)) {
                startFolderUpload((onFound) => walkEntries(entries, onFound));
                return;
            }

            await uploadFiles(e.dataTransfer.files);
        });

        // File input button — upload via fetch instead of Blazor InputFile
        const fileInput = document.getElementById('fileUploadInput');
        if (fileInput) {
            fileInput.addEventListener('change', async () => {
                await uploadFiles(fileInput.files);
                fileInput.value = ''; // Reset so the same file can be re-selected
            });
        }
    }

    // Try immediately
    const existing = document.getElementById(dropZoneId);
    if (existing) {
        bindEvents(existing);
        return;
    }

    // Element not in DOM yet — watch for it with MutationObserver
    const observer = new MutationObserver(() => {
        const el = document.getElementById(dropZoneId);
        if (el) {
            observer.disconnect();
            bindEvents(el);
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });
}
