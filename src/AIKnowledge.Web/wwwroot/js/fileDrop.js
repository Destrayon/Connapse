// File drop zone JS interop for Blazor Server
// Handles native file drag-and-drop by uploading directly via fetch(),
// bypassing InputFile which has cross-browser issues with synthetic events.

// LAYER 1: Document-level prevention — stops the browser from EVER
// navigating to a dropped file, regardless of element state.
document.addEventListener('dragover', (e) => e.preventDefault());
document.addEventListener('drop', (e) => e.preventDefault());

export function initializeDropZone(dropZoneId, dotNetRef, containerId) {
    let dragCounter = 0;
    let bound = false;

    async function uploadFiles(files) {
        if (files.length === 0) return;

        const currentPath = await dotNetRef.invokeMethodAsync('GetCurrentPath');

        const formData = new FormData();
        for (let i = 0; i < files.length; i++) {
            formData.append('files', files[i]);
        }
        formData.append('path', currentPath);

        try {
            const response = await fetch(`/api/containers/${containerId}/files`, {
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
