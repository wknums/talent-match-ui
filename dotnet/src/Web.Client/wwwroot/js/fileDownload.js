window.talentMatchDownloadBase64 = (fileName, contentType, base64Content) => {
    const binary = atob(base64Content);
    const bytes = new Uint8Array(binary.length);

    for (let index = 0; index < binary.length; index += 1) {
        bytes[index] = binary.charCodeAt(index);
    }

    const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
    const anchor = document.createElement('a');

    try {
        anchor.href = url;
        anchor.download = fileName;
        document.body.appendChild(anchor);
        anchor.click();
    } finally {
        anchor.remove();
        window.setTimeout(() => URL.revokeObjectURL(url), 0);
    }
};
