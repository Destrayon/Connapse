window.connapse = window.connapse || {};

window.connapse.copyToClipboard = async function (text) {
    if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
        return true;
    }

    // Fallback for non-secure contexts (e.g. HTTP during dev)
    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.style.position = "fixed";
    textarea.style.left = "-9999px";
    document.body.appendChild(textarea);
    textarea.select();
    try {
        document.execCommand("copy");
        return true;
    } catch {
        return false;
    } finally {
        document.body.removeChild(textarea);
    }
};
