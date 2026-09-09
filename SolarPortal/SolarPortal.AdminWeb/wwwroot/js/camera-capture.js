/* ============================================================================
   camera-capture.js — a real "📷 Camera" button on every document upload.
   ----------------------------------------------------------------------------
   photo-source.js already offers Camera / Gallery, but ONLY on a phone: on a
   desktop the `capture` attribute is ignored, so an admin or member sitting at a
   PC had no way to take the photo — they had to shoot it on a phone and copy the
   file across. This adds a camera that works in both places:

       • phone   → opens the OS camera (capture input)
       • desktop → opens the webcam in a small modal (getUserMedia), and the shot
                   is turned into a JPEG File

   Either way the photo is handed to the ORIGINAL input via DataTransfer, so
   name= / asp-for binding, previews, validation and any change handler the page
   already has keep working untouched.

   Usage:  <input type="file" name="receiptImage" accept="image/*,.pdf" data-camera />
   The button is inserted right after the input. Style it with
   data-camera-class="btn btn-sm btn-p"; label it with data-camera-label="...".

   Pages that upload through their own hidden inputs can call
   window.openCameraFor(inputElement) directly.
   ========================================================================== */
(function () {
    'use strict';

    var canTransfer = (function () {
        try { return typeof DataTransfer === 'function' && 'items' in new DataTransfer(); }
        catch (e) { return false; }
    })();

    function isMobileLike() {
        try {
            if (window.matchMedia && window.matchMedia('(pointer: coarse)').matches) return true;
        } catch (e) { /* older browser — fall through */ }
        return /Android|iPhone|iPad|iPod|Windows Phone|Mobile/i.test(navigator.userAgent || '');
    }

    function giveFileToInput(input, file) {
        if (!canTransfer) {
            alert('This browser cannot attach a captured photo. Please use the file button instead.');
            return false;
        }
        var dt = new DataTransfer();
        dt.items.add(file);
        input.files = dt.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
    }

    // ── phone: hand the job to the OS camera ────────────────────────────────
    function openNativeCamera(input) {
        var cam = input.__nativeCam;
        if (!cam) {
            cam = document.createElement('input');
            cam.type = 'file';
            cam.accept = 'image/*';
            cam.setAttribute('capture', 'environment');
            cam.style.display = 'none';
            cam.addEventListener('change', function () {
                if (cam.files && cam.files.length) giveFileToInput(input, cam.files[0]);
            });
            input.parentNode.insertBefore(cam, input.nextSibling);
            input.__nativeCam = cam;
        }
        cam.value = '';
        cam.click();
    }

    // ── desktop: webcam in a modal ──────────────────────────────────────────
    var modal = null, videoEl = null, stream = null, target = null;

    function buildModal() {
        modal = document.createElement('div');
        modal.style.cssText =
            'position:fixed;inset:0;z-index:99999;background:rgba(15,23,42,.75);' +
            'display:none;align-items:center;justify-content:center;padding:16px';
        modal.innerHTML =
            '<div style="background:#fff;border-radius:14px;max-width:640px;width:100%;overflow:hidden;box-shadow:0 20px 45px rgba(0,0,0,.35)">' +
              '<div style="padding:12px 16px;font-weight:700;font-size:15px;border-bottom:1px solid #e5e7eb">📷 Take a photo</div>' +
              '<div style="background:#000"><video autoplay playsinline muted style="width:100%;max-height:60vh;display:block"></video></div>' +
              '<div data-cc-msg style="display:none;padding:14px 16px;color:#b91c1c;font-size:13px"></div>' +
              '<div style="display:flex;gap:8px;justify-content:flex-end;padding:12px 16px">' +
                '<button type="button" data-cc-cancel style="padding:8px 16px;border:1px solid #d1d5db;background:#fff;border-radius:8px;cursor:pointer">Cancel</button>' +
                '<button type="button" data-cc-shoot style="padding:8px 18px;border:0;background:#10b981;color:#fff;border-radius:8px;font-weight:600;cursor:pointer">Capture</button>' +
              '</div>' +
            '</div>';
        document.body.appendChild(modal);
        videoEl = modal.querySelector('video');
        modal.querySelector('[data-cc-cancel]').addEventListener('click', closeModal);
        modal.querySelector('[data-cc-shoot]').addEventListener('click', shoot);
        modal.addEventListener('click', function (e) { if (e.target === modal) closeModal(); });
    }

    function showError(text) {
        var box = modal.querySelector('[data-cc-msg]');
        box.textContent = text;
        box.style.display = '';
        modal.querySelector('[data-cc-shoot]').style.display = 'none';
    }

    function closeModal() {
        if (stream) { stream.getTracks().forEach(function (t) { t.stop(); }); stream = null; }
        if (modal) modal.style.display = 'none';
        target = null;
    }

    function shoot() {
        if (!stream || !target) return;
        var c = document.createElement('canvas');
        c.width = videoEl.videoWidth || 1280;
        c.height = videoEl.videoHeight || 720;
        c.getContext('2d').drawImage(videoEl, 0, 0, c.width, c.height);
        var input = target;
        c.toBlob(function (blob) {
            if (blob) {
                var name = 'camera-' + Date.now() + '.jpg';
                giveFileToInput(input, new File([blob], name, { type: 'image/jpeg' }));
            }
            closeModal();
        }, 'image/jpeg', 0.92);
    }

    function openWebcam(input) {
        if (!modal) buildModal();
        target = input;
        var box = modal.querySelector('[data-cc-msg]');
        box.style.display = 'none';
        modal.querySelector('[data-cc-shoot]').style.display = '';
        modal.style.display = 'flex';

        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            showError('This browser cannot open a camera. Please use the file button instead.');
            return;
        }
        navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false })
            .then(function (s) { stream = s; videoEl.srcObject = s; })
            .catch(function () {
                showError('Camera not available — it may be blocked for this site, or in use by another app. Please allow camera access, or use the file button.');
            });
    }

    window.openCameraFor = function (input) {
        if (!input) return;
        if (isMobileLike()) openNativeCamera(input); else openWebcam(input);
    };

    // ── button next to every data-camera input ──────────────────────────────
    function enhance(input) {
        if (input.dataset.cameraReady === '1') return;
        input.dataset.cameraReady = '1';

        var btn = document.createElement('button');
        btn.type = 'button';                  // never submits the surrounding form
        btn.className = input.dataset.cameraClass || 'btn btn-sm btn-s';
        btn.textContent = input.dataset.cameraLabel || '📷 Camera';
        btn.style.marginTop = '6px';
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();              // some upload areas re-open the file dialog on click
            window.openCameraFor(input);
        });
        input.insertAdjacentElement('afterend', btn);
    }

    window.enhanceCameraInputs = function (root) {
        (root || document).querySelectorAll('input[type="file"][data-camera]').forEach(enhance);
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { window.enhanceCameraInputs(); });
    } else {
        window.enhanceCameraInputs();
    }
})();
