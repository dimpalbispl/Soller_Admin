/*
 * Camera / Gallery choice on every photo upload (change request point 5:
 * "jahan bhi photo upload ho rahi hai wahan Camera / Gallery ka option dena hai").
 *
 * Why two buttons instead of one input: a bare <input type="file"> leaves the
 * choice to the phone, and several Android builds open the gallery with no way
 * to reach the camera. Adding capture="environment" forces the camera but then
 * an existing file cannot be picked. So we keep the ORIGINAL input as the
 * gallery path — its name, asp-for binding and validation stay exactly as they
 * were, so nothing on the server changes — and add a hidden capture input for
 * the camera, copying whatever it produces into the original via DataTransfer.
 *
 * Usage: add data-photo-picker to any file input.
 *      <input type="file" name="receiptImage" accept="image/*,.pdf" data-photo-picker />
 *
 * Runs on DOMContentLoaded and can be re-run (enhancePhotoPickers()) after
 * injecting markup dynamically; already-enhanced inputs are skipped.
 */
(function () {
    'use strict';

    // Some browsers (older Safari, a few Android webviews) have no DataTransfer
    // constructor, so a captured photo cannot be moved into the original input.
    // There we fall back to submitting the camera input itself, which the form
    // still picks up because it carries the same name.
    var canTransfer = (function () {
        try { return typeof DataTransfer === 'function' && 'items' in new DataTransfer(); }
        catch (e) { return false; }
    })();

    function label(input) {
        var el = input.parentNode.querySelector('.pp-name');
        if (!el) return;
        var f = input.files && input.files[0];
        el.textContent = f ? f.name : 'No file chosen';
        el.style.color = f ? 'var(--text2, #475569)' : 'var(--text3, #94a3b8)';
    }

    function enhance(input) {
        if (input.dataset.ppReady === '1') return;
        input.dataset.ppReady = '1';

        var wrap = document.createElement('div');
        wrap.className = 'pp-wrap';
        wrap.style.cssText = 'display:flex;flex-direction:column;gap:4px';
        input.parentNode.insertBefore(wrap, input);

        var bar = document.createElement('div');
        bar.style.cssText = 'display:flex;gap:8px;flex-wrap:wrap';

        var camBtn = document.createElement('button');
        camBtn.type = 'button';
        camBtn.className = 'btn btn-s btn-sm';
        camBtn.textContent = '📷 Camera';

        var galBtn = document.createElement('button');
        galBtn.type = 'button';
        galBtn.className = 'btn btn-s btn-sm';
        galBtn.textContent = '🖼️ Gallery';

        var name = document.createElement('div');
        name.className = 'pp-name';
        name.style.cssText = 'font-size:11px;color:var(--text3,#94a3b8)';
        name.textContent = 'No file chosen';

        // The camera input mirrors the original's name so that, on a browser
        // without DataTransfer, the form still posts the captured photo.
        var cam = document.createElement('input');
        cam.type = 'file';
        cam.accept = 'image/*';
        cam.setAttribute('capture', 'environment');
        cam.style.display = 'none';
        if (input.multiple) cam.multiple = false;   // one shot at a time
        if (!canTransfer && input.name) cam.name = input.name;

        bar.appendChild(camBtn);
        bar.appendChild(galBtn);
        wrap.appendChild(bar);
        wrap.appendChild(cam);
        wrap.appendChild(name);

        // Keep the original in the DOM (it owns the name and any validation),
        // just out of sight — the two buttons drive it now.
        input.style.display = 'none';
        wrap.appendChild(input);

        galBtn.addEventListener('click', function () { input.click(); });
        camBtn.addEventListener('click', function () { cam.click(); });

        input.addEventListener('change', function () {
            cam.value = '';                 // gallery wins; clear any stale capture
            label(input);
        });

        cam.addEventListener('change', function () {
            if (canTransfer && cam.files && cam.files.length) {
                var dt = new DataTransfer();
                for (var i = 0; i < cam.files.length; i++) dt.items.add(cam.files[i]);
                input.files = dt.files;
                cam.value = '';             // the original now owns the file
                label(input);
            } else if (cam.files && cam.files[0]) {
                var el = wrap.querySelector('.pp-name');
                if (el) el.textContent = cam.files[0].name;
            }
        });
    }

    function enhancePhotoPickers(root) {
        (root || document).querySelectorAll('input[type="file"][data-photo-picker]').forEach(enhance);
    }

    window.enhancePhotoPickers = enhancePhotoPickers;
    document.addEventListener('DOMContentLoaded', function () { enhancePhotoPickers(); });
})();
