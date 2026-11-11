// Helpers used by DocumentViewer.razor to render binary office formats client-side.
window.docxToHtml = async function (base64) {
    if (!base64 || typeof mammoth === 'undefined') return null;
    try {
        const binary = atob(base64);
        const len = binary.length;
        const bytes = new Uint8Array(len);
        for (let i = 0; i < len; i++) bytes[i] = binary.charCodeAt(i);
        const result = await mammoth.convertToHtml({ arrayBuffer: bytes.buffer });
        return result && result.value ? result.value : null;
    } catch (err) {
        console.warn('docxToHtml failed', err);
        return null;
    }
};
