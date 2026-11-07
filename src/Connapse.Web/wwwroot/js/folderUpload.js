// Uploads a whole folder to a container. No DOM and no Blazor in here, so tests/Connapse.Web.Js runs this same file
// under Node against a real folder; fileDrop.js connects it to the page.
//
// A browser can't send a large folder in one request: the API takes at most 100 files a request, a request's size is
// limited, and a call into Blazor is limited to 32 KB. So files go in small batches, each file naming its own folder,
// and the full list of failures stays here while the page gets counts and the first few. MAX_BYTES_PER_REQUEST stays
// under the smallest body limit the server can have; a larger file goes alone, up to the upload limit.

export const MAX_FILES_PER_REQUEST = 100;
export const MAX_BYTES_PER_REQUEST = 20 * 1024 * 1024;
export const CONCURRENT_REQUESTS = 3;
export const LARGE_FOLDER_FILES = 5000;

// Requests that fail one after another mean the server is gone, not that the files are bad.
const MAX_FAILED_REQUESTS_IN_A_ROW = 10;
const MAX_RATE_LIMIT_WAITS = 8;
const DEFAULT_RATE_LIMIT_WAIT_MS = 5000;
const FILES_READ_AT_ONCE = 16;

// An entry of a dropped folder: FileSystemFileEntry or FileSystemDirectoryEntry. readEntries gives at most 100 at a
// time, and only an empty answer means the folder is finished.
function readAllEntries(directory) {
    const reader = directory.createReader();
    return new Promise((resolve, reject) => {
        const entries = [];
        const readMore = () => reader.readEntries(batch => {
            if (batch.length === 0) {
                resolve(entries);
                return;
            }
            entries.push(...batch);
            readMore();
        }, reject);
        readMore();
    });
}

const fileOf = entry => new Promise((resolve, reject) => entry.file(resolve, reject));

const relativePathOf = entry => entry.fullPath.replace(/^\/+/, '');

// A browser's read of a slow or stuck disk may never call back, and Cancel still has to end the run.
function untilAborted(read, signal) {
    return new Promise((resolve, reject) => {
        const stop = () => reject(signal.reason);
        if (signal.aborted) stop();
        signal.addEventListener('abort', stop, { once: true });
        read.then(resolve, reject).finally(() => signal.removeEventListener('abort', stop));
    });
}

/**
 * Walks dropped entries. What can't be read, such as a file removed since the drop, is reported rather than stopping
 * the walk. Aborting ends it at once with what it has. Answers { files: [{ file, relativePath }], unreadable: [{ relativePath, error }] }.
 */
export async function walkEntries(entries, onProgress = () => { }, signal = new AbortController().signal) {
    const files = [];
    const unreadable = [];
    const fileEntries = [];
    const pending = [...entries];

    while (pending.length > 0 && !signal.aborted) {
        const entry = pending.pop();
        if (entry.isFile) {
            fileEntries.push(entry);
            continue;
        }
        try {
            pending.push(...await untilAborted(readAllEntries(entry), signal));
        } catch (err) {
            if (!signal.aborted) unreadable.push({ relativePath: relativePathOf(entry), error: `This folder can't be read: ${err?.message ?? err}` });
        }
    }

    let next = 0;
    async function readFiles() {
        while (next < fileEntries.length && !signal.aborted) {
            const entry = fileEntries[next++];
            try {
                files.push({ file: await untilAborted(fileOf(entry), signal), relativePath: relativePathOf(entry) });
            } catch (err) {
                if (!signal.aborted) unreadable.push({ relativePath: relativePathOf(entry), error: `This file can't be read: ${err?.message ?? err}` });
            }
            onProgress(files.length + unreadable.length);
        }
    }
    await Promise.all(Array.from({ length: FILES_READ_AT_ONCE }, readFiles));

    return { files, unreadable };
}

/** Files from a folder picker, each already carrying its path under the chosen folder. */
export function fromFolderPicker(fileList) {
    return Array.from(fileList, file => ({ file, relativePath: file.webkitRelativePath || file.name }));
}

/** The folder a file lands in: the folder being viewed plus the file's own subfolders. Base is '/' or '/sub/'. */
export function folderFor(basePath, relativePath) {
    const lastSlash = relativePath.lastIndexOf('/');
    return lastSlash < 0 ? basePath : `${basePath}${relativePath.slice(0, lastSlash)}/`;
}

const extensionOf = name => {
    const dot = name.lastIndexOf('.');
    return dot < 0 ? '' : name.slice(dot).toLowerCase();
};

// The server reads a backslash as a folder break and resolves dot segments, so a folder named a\b would land in a/b
// and could silently replace a file there. Mac and Linux allow that name.
const folderNameChangesOnServer = relativePath => relativePath.split('/').slice(0, -1)
    .some(folder => folder.includes('\\') || folder === '.' || folder === '..');

/** Splits files into those worth sending and those the server would refuse for their type, sent or not. */
export function splitByType(items, supportedExtensions) {
    const supported = new Set(supportedExtensions.map(e => e.toLowerCase()));
    const send = [];
    const refused = [];
    for (const item of items) {
        (supported.has(extensionOf(item.file.name)) ? send : refused).push(item);
    }
    return { send, refused };
}

/** The server's wording for the same refusal, rounded up so a file one byte over doesn't read as being at the limit. */
export function fileTooLarge(bytes, maxFileBytes) {
    const megabytes = size => Math.ceil(size / (1024 * 1024) * 10) / 10;
    return `The file is ${megabytes(bytes)} MB, over the ${megabytes(maxFileBytes)} MB limit.`;
}

/** Splits files into those worth sending and those the server would refuse for their size. */
export function splitBySize(items, maxFileBytes) {
    const send = [];
    const refused = [];
    for (const item of items) {
        (item.file.size > maxFileBytes ? refused : send).push(item);
    }
    return { send, refused };
}

/** Batches within the API's file count and the server's body limit. A file over the byte limit goes alone. */
export function planBatches(items, maxFiles = MAX_FILES_PER_REQUEST, maxBytes = MAX_BYTES_PER_REQUEST) {
    const batches = [];
    let batch = [];
    let bytes = 0;
    for (const item of items) {
        if (batch.length > 0 && (batch.length >= maxFiles || bytes + item.file.size > maxBytes)) {
            batches.push(batch);
            batch = [];
            bytes = 0;
        }
        batch.push(item);
        bytes += item.file.size;
    }
    if (batch.length > 0) batches.push(batch);
    return batches;
}

function abortableWait(ms, signal) {
    return new Promise(resolve => {
        if (signal.aborted) {
            resolve();
            return;
        }
        const done = () => {
            clearTimeout(timer);
            signal.removeEventListener('abort', done);
            resolve();
        };
        const timer = setTimeout(done, ms);
        signal.addEventListener('abort', done);
    });
}

async function problemDetail(response, fallback = `The server answered ${response.status}.`) {
    try {
        const problem = await response.json();
        return problem.detail ?? problem.title ?? fallback;
    } catch {
        return fallback;
    }
}

/** A request that failed as a whole. Nothing in it says which file was at fault, so it can be split to find out. */
class BatchFailed extends Error { }

/** Says nothing about the server's health: a folder of large files is refused one by one without anything being wrong. */
class TooLarge extends BatchFailed { }

/** A refusal that would be the same for every file, such as a sign-in that ended. The run stops here. */
class RunRefused extends Error { }

/**
 * Sends one batch. Answers the API's documents, in the order of the batch.
 * The file name is passed to the form on purpose: left out, a browser sends a picked file's whole relative path as its
 * name, which the API refuses.
 */
async function sendBatch(batch, { url, basePath, fetchImpl, signal, wait, onWaiting }) {
    for (let attempt = 0; ; attempt++) {
        const form = new FormData();
        for (const { file, relativePath } of batch) {
            form.append('files', file, file.name);
            form.append('paths', folderFor(basePath, relativePath));
        }

        let response;
        try {
            response = await fetchImpl(url, { method: 'POST', body: form, signal });
        } catch (err) {
            if (signal.aborted) throw err;
            throw new BatchFailed(`The upload was interrupted: ${err?.message ?? err}`);
        }

        if (response.ok) return (await response.json()).documents;

        if (response.status === 429 && attempt < MAX_RATE_LIMIT_WAITS) {
            const seconds = Number(response.headers.get('Retry-After'));
            onWaiting();
            await wait(Number.isFinite(seconds) && seconds > 0 ? seconds * 1000 : DEFAULT_RATE_LIMIT_WAIT_MS, signal);
            if (signal.aborted) throw new DOMException('Aborted', 'AbortError');
            continue;
        }
        // These would be the same for every file left. Anything else may come from one file in the batch.
        if (response.status === 401 || response.status === 403 || response.status === 404) {
            throw new RunRefused(await problemDetail(response));
        }
        // A reverse proxy's own 413 has no detail to show.
        if (response.status === 413) throw new TooLarge(await problemDetail(response, 'This file is larger than the server accepts in one request.'));
        throw new BatchFailed(`The server couldn't take this file: ${await problemDetail(response)}`);
    }
}

/**
 * Uploads items, reporting each one's outcome. One bad file costs only that file: a failed batch is split in half
 * until the file at fault is alone.
 *
 * Options: url, basePath ('/' or '/sub/'), fetchImpl, signal, onOutcome(item, error|null), onWaiting(), and optionally
 * onSent(documents), wait(ms, signal), concurrency, maxFiles, maxBytes.
 * Answers null, or why the run stopped early; every file not attempted has then had a failed outcome.
 */
export async function uploadItems(items, options) {
    const {
        url, basePath, signal, onOutcome,
        fetchImpl = fetch,
        onWaiting = () => { },
        onSent = () => { },
        wait = abortableWait,
        concurrency = CONCURRENT_REQUESTS,
        maxFiles = MAX_FILES_PER_REQUEST,
        maxBytes = MAX_BYTES_PER_REQUEST,
    } = options;

    const queue = planBatches(items, maxFiles, maxBytes);
    const sendOptions = { url, basePath, fetchImpl, signal, wait, onWaiting };
    let failedInARow = 0;
    let stoppedBecause = null;

    function stop(reason) {
        stoppedBecause ??= reason;
        for (const batch of queue.splice(0)) {
            for (const item of batch) onOutcome(item, stoppedBecause);
        }
    }

    async function work() {
        while (queue.length > 0) {
            if (signal.aborted) return;
            const batch = queue.shift();
            try {
                const documents = await sendBatch(batch, sendOptions);
                failedInARow = 0;
                await onSent(documents);
                batch.forEach((item, i) => onOutcome(item, documents[i]?.error ?? null));
            } catch (err) {
                if (signal.aborted) {
                    queue.unshift(batch);
                    return;
                }
                if (err instanceof BatchFailed) {
                    if (batch.length > 1) {
                        // Ahead of the rest, so the files that fail are found before more are sent.
                        queue.unshift(batch.slice(0, batch.length >> 1), batch.slice(batch.length >> 1));
                    } else {
                        onOutcome(batch[0], err.message);
                    }
                    if (!(err instanceof TooLarge) && ++failedInARow >= MAX_FAILED_REQUESTS_IN_A_ROW) {
                        stop(`The upload stopped because the server kept failing. Last error: ${err.message}`);
                    }
                } else if (err instanceof RunRefused) {
                    batch.forEach(item => onOutcome(item, err.message));
                    stop(err.message);
                } else {
                    batch.forEach(item => onOutcome(item, `This file couldn't be sent: ${err?.message ?? err}`));
                }
            }
        }
    }

    await Promise.all(Array.from({ length: concurrency }, work));

    if (signal.aborted) {
        for (const batch of queue.splice(0)) {
            for (const item of batch) onOutcome(item, 'The upload was cancelled before this file finished.');
        }
    }
    return stoppedBecause;
}

/**
 * One folder upload from the files found to the final count. Options are uploadItems' plus unreadable (from walkEntries),
 * supportedExtensions, maxFileBytes and onProgress(state). State is { total, succeeded, failures: [{ path, error }], waiting }.
 * Answers the state, with stoppedBecause set when the run ended early.
 */
export async function runFolderUpload(items, options) {
    const { unreadable = [], supportedExtensions, maxFileBytes = Infinity, onProgress = () => { }, basePath } = options;
    const destination = relativePath => `${folderFor(basePath, relativePath)}${relativePath.slice(relativePath.lastIndexOf('/') + 1)}`;
    const state = { total: items.length + unreadable.length, succeeded: 0, failures: [], waiting: false, stoppedBecause: null };
    const fail = (relativePath, error) => state.failures.push({ path: destination(relativePath), error });

    for (const { relativePath, error } of unreadable) fail(relativePath, error);
    const { send, refused } = splitByType(items, supportedExtensions);
    for (const { relativePath, file } of refused) {
        fail(relativePath, extensionOf(file.name) ? `Connapse can't read ${extensionOf(file.name)} files.` : "Connapse can't read files with no extension.");
    }
    const { send: withinLimit, refused: tooLarge } = splitBySize(send, maxFileBytes);
    for (const { relativePath, file } of tooLarge) fail(relativePath, fileTooLarge(file.size, maxFileBytes));
    const safe = withinLimit.filter(item => !folderNameChangesOnServer(item.relativePath));
    for (const { relativePath } of withinLimit.filter(item => folderNameChangesOnServer(item.relativePath))) {
        fail(relativePath, "Connapse can't keep this folder's name, because it has a backslash or is only dots.");
    }
    onProgress(state);

    state.stoppedBecause = await uploadItems(safe, {
        ...options,
        onWaiting() {
            state.waiting = true;
            onProgress(state);
        },
        onOutcome(item, error) {
            state.waiting = false;
            if (error) fail(item.relativePath, error);
            else state.succeeded++;
            onProgress(state);
        },
    });
    return state;
}

/** The failures as text: a tab between path and reason, one file a line. */
export function failureReport(failures) {
    return failures.map(({ path, error }) => `${path}\t${error}`).join('\n');
}
