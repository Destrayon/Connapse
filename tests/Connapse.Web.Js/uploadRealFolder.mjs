// Sends a folder from disk through the same code a browser runs. FolderUploadTests starts this.
// Usage: node uploadRealFolder.mjs <folder> <upload url> <base path> <comma-separated extensions>
// Prints the finished run as JSON.
import { openAsBlob } from 'node:fs';
import { readdir } from 'node:fs/promises';
import { basename, join, relative, sep } from 'node:path';
import { runFolderUpload } from '../../src/Connapse.Web/wwwroot/js/folderUpload.js';

const [folder, url, basePath, extensions] = process.argv.slice(2);

const entries = await readdir(folder, { recursive: true, withFileTypes: true });
const items = await Promise.all(entries.filter(entry => entry.isFile()).map(async entry => {
    const path = join(entry.parentPath, entry.name);
    return {
        file: new File([await openAsBlob(path)], entry.name),
        // A folder picker names each file from the chosen folder down.
        relativePath: [basename(folder), ...relative(folder, path).split(sep)].join('/'),
    };
}));

const finished = await runFolderUpload(items, {
    url,
    basePath,
    supportedExtensions: extensions.split(','),
    signal: new AbortController().signal,
});
console.log(JSON.stringify(finished));
