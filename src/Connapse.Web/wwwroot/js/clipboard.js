window.connapse = window.connapse || {};

// Scrolls to an element by id, for links that point somewhere on the page they are already on.
//
// Neither href form works for that here. The full path is intercepted by Blazor's router, which
// re-navigates without scrolling; a bare "#id" is resolved against <base href="/"> and lands on the
// home page instead. Both looked like a link that did nothing, and the second one silently went
// somewhere else.
window.connapse.scrollToId = function (id) {
    const target = document.getElementById(id);
    if (!target) return false;

    target.scrollIntoView({ behavior: "smooth", block: "start" });
    return true;
};

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
