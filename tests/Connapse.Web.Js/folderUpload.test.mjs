import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import {
    failureReport, folderFor, fromFolderPicker, planBatches, runFolderUpload, splitByType, uploadItems, walkEntries,
} from '../../src/Connapse.Web/wwwroot/js/folderUpload.js';

const fileNamed = (name, size = 10) => new File(['x'.repeat(size)], name);
const item = (relativePath, size) => ({ file: fileNamed(relativePath.split('/').pop(), size), relativePath });
const items = (count, size) => Array.from({ length: count }, (_, i) => item(`docs/${i}.md`, size));

const fileEntry = (fullPath, failure) => ({
    isFile: true,
    fullPath,
    file: (resolve, reject) => (failure ? reject(failure) : resolve(fileNamed(fullPath.split('/').pop()))),
});

// Gives children in batches of at most 100, as browsers do, and only an empty answer ends it.
function directoryEntry(fullPath, children, failure) {
    return {
        isFile: false,
        fullPath,
        createReader() {
            const left = [...children];
            return {
                readEntries(resolve, reject) {
                    if (failure) reject(failure);
                    else resolve(left.splice(0, 100));
                },
            };
        },
    };
}

const json = (status, body, headers = {}) => new Response(JSON.stringify(body), { status, headers });

// A server that takes everything, recording what each request asked for.
function acceptingServer() {
    const requests = [];
    const fetchImpl = async (_url, { body }) => {
        const names = body.getAll('files').map(f => f.name);
        requests.push({ names, paths: body.getAll('paths') });
        return json(200, { documents: names.map(() => ({ error: null })) });
    };
    return { requests, fetchImpl };
}

async function upload(sent, fetchImpl, options = {}) {
    const outcomes = new Map();
    const stoppedBecause = await uploadItems(sent, {
        url: '/files',
        basePath: '/',
        fetchImpl,
        signal: new AbortController().signal,
        wait: async () => { },
        onOutcome: (i, error) => outcomes.set(i.relativePath, error),
        ...options,
    });
    return { outcomes, stoppedBecause };
}

describe('walkEntries', () => {
    test('finds every file in nested folders, past the 100 entries a single read gives', async () => {
        const many = Array.from({ length: 250 }, (_, i) => fileEntry(`/top/many/${i}.md`));
        const root = directoryEntry('/top', [
            fileEntry('/top/a.md'),
            directoryEntry('/top/many', many),
            directoryEntry('/top/empty', []),
        ]);

        const { files, unreadable } = await walkEntries([root]);

        assert.equal(files.length, 251);
        assert.deepEqual(unreadable, []);
        assert.ok(files.some(f => f.relativePath === 'top/many/249.md'));
        assert.ok(files.some(f => f.relativePath === 'top/a.md'));
    });

    test('reports what cannot be read and keeps walking', async () => {
        const root = directoryEntry('/top', [
            fileEntry('/top/gone.md', new Error('removed')),
            directoryEntry('/top/locked', [], new Error('denied')),
            fileEntry('/top/fine.md'),
        ]);

        const { files, unreadable } = await walkEntries([root]);

        assert.deepEqual(files.map(f => f.relativePath), ['top/fine.md']);
        assert.deepEqual(unreadable.map(u => u.relativePath).sort(), ['top/gone.md', 'top/locked']);
        assert.ok(unreadable.every(u => u.error.includes("can't be read")));
    });

    test('counts progress as files are read', async () => {
        const seen = [];
        await walkEntries([fileEntry('/a.md'), fileEntry('/b.md')], n => seen.push(n));
        assert.deepEqual(seen, [1, 2]);
    });
});

describe('walkEntries when cancelled', () => {
    const stuckDirectory = fullPath => ({ isFile: false, fullPath, createReader: () => ({ readEntries() { } }) });
    const stuckFile = fullPath => ({ isFile: true, fullPath, file() { } });

    test('ends at once while a folder read never answers, reporting nothing as unreadable', async () => {
        const abort = new AbortController();
        const walk = walkEntries([stuckDirectory('/top')], () => { }, abort.signal);

        abort.abort();

        assert.deepEqual(await walk, { files: [], unreadable: [] });
    });

    test('ends at once while a file read never answers, reporting nothing as unreadable', async () => {
        const abort = new AbortController();
        const walk = walkEntries([stuckFile('/a.md'), fileEntry('/b.md')], () => { }, abort.signal);
        await new Promise(resolve => setImmediate(resolve));

        abort.abort();
        const { unreadable } = await walk;

        assert.deepEqual(unreadable, []);
    });

    test('reads nothing when already cancelled', async () => {
        const abort = new AbortController();
        abort.abort();
        const read = [];
        const entry = { isFile: true, fullPath: '/a.md', file: () => read.push('a') };

        const { files, unreadable } = await walkEntries([entry], () => { }, abort.signal);

        assert.deepEqual([read, files, unreadable], [[], [], []]);
    });
});

test('fromFolderPicker takes each file\'s path under the chosen folder, or its name without one', () => {
    const nested = fileNamed('a.md');
    Object.defineProperty(nested, 'webkitRelativePath', { value: 'top/sub/a.md' });

    const found = fromFolderPicker([nested, fileNamed('b.md')]);

    assert.deepEqual(found.map(f => f.relativePath), ['top/sub/a.md', 'b.md']);
});

describe('folderFor', () => {
    test('puts a nested file under the folder being viewed', () => {
        assert.equal(folderFor('/', 'top/sub/a.md'), '/top/sub/');
        assert.equal(folderFor('/here/', 'top/a.md'), '/here/top/');
    });

    test('leaves a file with no folder in the folder being viewed', () => {
        assert.equal(folderFor('/here/', 'a.md'), '/here/');
    });
});

describe('splitByType', () => {
    test('goes by the extension whatever its case, as the server does', () => {
        const { send, refused } = splitByType([item('a.MD'), item('b.exe'), item('LICENSE'), item('.md')], ['.md']);

        assert.deepEqual(send.map(s => s.relativePath), ['a.MD', '.md']);
        assert.deepEqual(refused.map(r => r.relativePath), ['b.exe', 'LICENSE']);
    });
});

describe('planBatches', () => {
    test('keeps every batch within the file count', () => {
        assert.deepEqual(planBatches(items(250)).map(b => b.length), [100, 100, 50]);
    });

    test('starts a new batch rather than pass the byte limit', () => {
        assert.deepEqual(planBatches(items(5, 40), 100, 100).map(b => b.length), [2, 2, 1]);
    });

    test('sends a file over the byte limit alone', () => {
        const batches = planBatches([item('a.md', 10), item('big.md', 200), item('c.md', 10)], 100, 100);
        assert.deepEqual(batches.map(b => b.map(i => i.relativePath)), [['a.md'], ['big.md'], ['c.md']]);
    });

    test('has no batches for no files', () => {
        assert.deepEqual(planBatches([]), []);
    });
});

describe('uploadItems', () => {
    test('sends each file with its own name and folder, in the same order', async () => {
        const { requests, fetchImpl } = acceptingServer();

        await upload([item('top/sub/a.md'), item('top/b.md')], fetchImpl, { basePath: '/here/' });

        assert.deepEqual(requests, [{ names: ['a.md', 'b.md'], paths: ['/here/top/sub/', '/here/top/'] }]);
    });

    test('reports a file the server refused and the rest as uploaded', async () => {
        const fetchImpl = async () => json(200, { documents: [{ error: null }, { error: 'File is empty.' }] });

        const { outcomes, stoppedBecause } = await upload(items(2), fetchImpl);

        assert.equal(stoppedBecause, null);
        assert.deepEqual([...outcomes.values()], [null, 'File is empty.']);
    });

    test('splits a failed batch until the bad file is alone, and the rest arrive', async () => {
        const fetchImpl = async (_url, { body }) => {
            const names = body.getAll('files').map(f => f.name);
            return names.includes('3.md')
                ? json(500, { detail: 'broke' })
                : json(200, { documents: names.map(() => ({ error: null })) });
        };

        const { outcomes } = await upload(items(8), fetchImpl);

        assert.equal(outcomes.size, 8);
        assert.match(outcomes.get('docs/3.md'), /broke/);
        assert.equal([...outcomes.values()].filter(e => e === null).length, 7);
    });

    test('splits on a dropped connection too', async () => {
        const fetchImpl = async (_url, { body }) => {
            const names = body.getAll('files').map(f => f.name);
            if (names.includes('1.md')) throw new TypeError('network error');
            return json(200, { documents: names.map(() => ({ error: null })) });
        };

        const { outcomes } = await upload(items(2), fetchImpl);

        assert.equal(outcomes.get('docs/0.md'), null);
        assert.match(outcomes.get('docs/1.md'), /interrupted/);
    });

    test('a folder of files too large for a request does not count as the server failing', async () => {
        const { outcomes, stoppedBecause } = await upload(items(30), async () => new Response(null, { status: 413 }), { maxFiles: 1 });

        assert.equal(stoppedBecause, null);
        assert.equal(outcomes.size, 30);
        assert.ok([...outcomes.values()].every(e => e.includes('larger')));
    });

    test('stops after repeated failures and marks every file not attempted', async () => {
        let calls = 0;
        const fetchImpl = async () => {
            calls++;
            return new Response(null, { status: 503 });
        };

        const { outcomes, stoppedBecause } = await upload(items(50), fetchImpl, { maxFiles: 1, concurrency: 1 });

        assert.equal(calls, 10);
        assert.match(stoppedBecause, /kept failing/);
        assert.equal(outcomes.size, 50);
    });

    test('a success resets the failures counted in a row', async () => {
        let calls = 0;
        const fetchImpl = async (_url, { body }) => (++calls % 5 === 0
            ? json(200, { documents: body.getAll('files').map(() => ({ error: null })) })
            : new Response(null, { status: 503 }));

        const { stoppedBecause } = await upload(items(30), fetchImpl, { maxFiles: 1, concurrency: 1 });

        assert.equal(stoppedBecause, null);
    });

    for (const status of [401, 403, 404]) {
        test(`stops at once on ${status}, with the server's reason on every file`, async () => {
            let calls = 0;
            const fetchImpl = async () => {
                calls++;
                return json(status, { detail: 'Not allowed here.' });
            };

            const { outcomes, stoppedBecause } = await upload(items(250), fetchImpl, { concurrency: 1 });

            assert.equal(calls, 1);
            assert.equal(stoppedBecause, 'Not allowed here.');
            assert.equal(outcomes.size, 250);
            assert.ok([...outcomes.values()].every(e => e === 'Not allowed here.'));
        });
    }

    test('falls back to the status when a refusal has no readable reason', async () => {
        const { stoppedBecause } = await upload(items(1), async () => new Response('<html>', { status: 403 }));
        assert.equal(stoppedBecause, 'The server answered 403.');
    });

    test('waits as long as Retry-After says, then sends the same batch', async () => {
        const waits = [];
        let waiting = 0;
        let calls = 0;
        const fetchImpl = async (_url, { body }) => (++calls === 1
            ? json(429, {}, { 'Retry-After': '7' })
            : json(200, { documents: body.getAll('files').map(() => ({ error: null })) }));

        const { outcomes } = await upload(items(2), fetchImpl, {
            wait: async ms => waits.push(ms),
            onWaiting: () => waiting++,
        });

        assert.deepEqual(waits, [7000]);
        assert.equal(waiting, 1);
        assert.equal(outcomes.get('docs/1.md'), null);
    });

    test('waits a default time when Retry-After is missing', async () => {
        const waits = [];
        let calls = 0;
        const fetchImpl = async (_url, { body }) => (++calls === 1
            ? json(429, {})
            : json(200, { documents: body.getAll('files').map(() => ({ error: null })) }));

        await upload(items(1), fetchImpl, { wait: async ms => waits.push(ms) });

        assert.deepEqual(waits, [5000]);
    });

    test('gives up on a file still rate limited after its waits', async () => {
        const { outcomes } = await upload(items(1), async () => json(429, { detail: 'Slow down.' }));
        assert.match(outcomes.get('docs/0.md'), /Slow down/);
    });

    test('cancelling stops new requests and accounts for every file', async () => {
        const abort = new AbortController();
        let calls = 0;
        const fetchImpl = async (_url, { body }) => {
            if (++calls === 2) abort.abort();
            return json(200, { documents: body.getAll('files').map(() => ({ error: null })) });
        };

        const { outcomes } = await upload(items(300), fetchImpl, { signal: abort.signal, concurrency: 1 });

        assert.equal(calls, 2);
        assert.equal(outcomes.size, 300);
        assert.equal([...outcomes.values()].filter(e => e === null).length, 200);
        assert.equal([...outcomes.values()].filter(e => e?.includes('cancelled')).length, 100);
    });

    test('cancelling during a request reports its files as cancelled', async () => {
        const abort = new AbortController();
        const fetchImpl = async (_url, { signal }) => {
            abort.abort();
            signal.throwIfAborted();
        };

        const { outcomes } = await upload(items(2), fetchImpl, { signal: abort.signal });

        assert.ok([...outcomes.values()].every(e => e.includes('cancelled')));
    });

    test('cancelling during a rate limit wait ends the wait', async () => {
        const abort = new AbortController();
        const fetchImpl = async () => json(429, {}, { 'Retry-After': '3600' });
        const started = Date.now();

        const finished = upload(items(1), fetchImpl, { signal: abort.signal, wait: undefined });
        abort.abort();
        const { outcomes } = await finished;

        assert.ok(Date.now() - started < 3000);
        assert.match(outcomes.get('docs/0.md'), /cancelled/);
    });

    test('never sends more than the concurrency at once', async () => {
        let open = 0;
        let most = 0;
        const fetchImpl = async (_url, { body }) => {
            most = Math.max(most, ++open);
            await new Promise(resolve => setImmediate(resolve));
            open--;
            return json(200, { documents: body.getAll('files').map(() => ({ error: null })) });
        };

        await upload(items(40), fetchImpl, { maxFiles: 1, concurrency: 3 });

        assert.equal(most, 3);
    });
});

describe('runFolderUpload', () => {
    const run = (found, options) => runFolderUpload(found, {
        url: '/files',
        basePath: '/',
        supportedExtensions: ['.md'],
        signal: new AbortController().signal,
        wait: async () => { },
        ...options,
    });

    test('fails a file under a folder name the server would change, so it can\'t replace another file', async () => {
        const { requests, fetchImpl } = acceptingServer();

        const state = await run([item('top/a\\b/note.md'), item('top/a/b/note.md'), item('top/../note.md'), item('top/./note.md')], { fetchImpl });

        assert.equal(state.total, 4);
        assert.equal(state.succeeded, 1);
        assert.deepEqual(requests.flatMap(r => r.paths), ['/top/a/b/']);
        assert.deepEqual(state.failures.map(f => f.path), ['/top/a\\b/note.md', '/top/../note.md', '/top/./note.md']);
        assert.ok(state.failures.every(f => f.error.includes("can't keep this folder's name")));
    });

    test('keeps a folder name that only has dots among other characters', async () => {
        const { requests, fetchImpl } = acceptingServer();

        const state = await run([item('top/.github/v1.2/note.md')], { fetchImpl });

        assert.equal(state.succeeded, 1);
        assert.deepEqual(requests.flatMap(r => r.paths), ['/top/.github/v1.2/']);
    });

    test('counts every file, and names each unsupported or unreadable one without sending it', async () => {
        const { requests, fetchImpl } = acceptingServer();

        const state = await run([item('top/a.md'), item('top/tool.exe'), item('top/LICENSE')], {
            fetchImpl,
            basePath: '/here/',
            unreadable: [{ relativePath: 'top/gone.md', error: "This file can't be read: removed" }],
        });

        assert.equal(state.total, 4);
        assert.equal(state.succeeded, 1);
        assert.deepEqual(requests.flatMap(r => r.names), ['a.md']);
        assert.deepEqual(state.failures, [
            { path: '/here/top/gone.md', error: "This file can't be read: removed" },
            { path: '/here/top/tool.exe', error: "Connapse can't read .exe files." },
            { path: '/here/top/LICENSE', error: "Connapse can't read files with no extension." },
        ]);
    });

    test('names a file at the top of a folder under the folder being viewed', async () => {
        const state = await run([item('tool.exe')], { fetchImpl: acceptingServer().fetchImpl, basePath: '/here/' });
        assert.equal(state.failures[0].path, '/here/tool.exe');
    });

    test('reports progress and clears the waiting flag once a file lands', async () => {
        const seen = [];
        let calls = 0;
        const fetchImpl = async (_url, { body }) => (++calls === 1
            ? json(429, {}, { 'Retry-After': '1' })
            : json(200, { documents: body.getAll('files').map(() => ({ error: null })) }));

        await run([item('a.md')], { fetchImpl, onProgress: s => seen.push(`${s.succeeded}:${s.waiting}`) });

        assert.deepEqual(seen, ['0:false', '0:true', '1:false']);
    });

    test('says why a run ended early', async () => {
        const state = await run(items(1), { fetchImpl: async () => json(403, { detail: 'No.' }) });
        assert.equal(state.stoppedBecause, 'No.');
    });
});

test('failureReport writes one file a line', () => {
    assert.equal(failureReport([{ path: '/a.md', error: 'bad' }, { path: '/b.md', error: 'worse' }]), '/a.md\tbad\n/b.md\tworse');
});
