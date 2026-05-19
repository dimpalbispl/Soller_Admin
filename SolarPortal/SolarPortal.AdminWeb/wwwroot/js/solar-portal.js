/* solar-portal.js — Main JavaScript for Solar Connection Portal */

// ===== UTILITY =====
function showError(msg) {
    if (typeof Swal !== 'undefined') {
        Swal.fire({ icon: 'error', title: 'Error', text: msg, background: '#ffffff', color: '#1f2937', confirmButtonColor: '#16a34a' });
    } else { alert(msg); }
}
function showSuccess(msg) {
    if (typeof Swal !== 'undefined') {
        Swal.fire({ icon: 'success', title: 'Success', text: msg, background: '#ffffff', color: '#1f2937', confirmButtonColor: '#16a34a', timer: 2500, showConfirmButton: false });
    } else { alert(msg); }
}
function showConfirm(msg, cb) {
    if (typeof Swal !== 'undefined') {
        Swal.fire({
            title: 'Confirm', text: msg, icon: 'question',
            showCancelButton: true, confirmButtonColor: '#16a34a', cancelButtonColor: '#6b7280',
            background: '#ffffff', color: '#1f2937'
        }).then(r => { if (r.isConfirmed) cb(); });
    } else { if (confirm(msg)) cb(); }
}

// ===== TAB SWITCHING =====
function switchTab(prefix, idx, el) {
    document.querySelectorAll(`#${prefix}-tabs .tab`).forEach(t => t.classList.remove('active'));
    document.querySelectorAll(`.tc[id^="${prefix}-"]`).forEach(t => t.classList.remove('active'));
    if (el) el.classList.add('active');
    const tc = document.getElementById(`${prefix}-${idx}`);
    if (tc) tc.classList.add('active');
}

// ===== FILE TRIGGER =====
function triggerFile(id) {
    const el = document.getElementById(id);
    if (el) el.click();
}

// ===== FILE PREVIEW =====
function previewSingle(inputId, previewId) {
    const input = document.getElementById(inputId);
    const preview = document.getElementById(previewId);
    if (!input || !preview || !input.files[0]) return;
    const file = input.files[0];
    preview.innerHTML = '';
    if (file.type.startsWith('image/')) {
        const img = document.createElement('img');
        img.style.cssText = 'max-width:100%;max-height:150px;border-radius:6px;margin-top:8px;border:1px solid #2a3350;';
        img.src = URL.createObjectURL(file);
        preview.appendChild(img);
    } else if (file.type === 'application/pdf') {
        preview.innerHTML = `<div style="margin-top:8px;padding:10px;background:#1e2535;border-radius:6px;border:1px solid #2a3350;font-size:12px;color:#a8b5d0;">📄 ${file.name} (${(file.size/1024).toFixed(1)} KB)</div>`;
    }
}

function previewDoc(inputId, previewId, docType) {
    previewSingle(inputId, previewId);
    // OCR simulation for Aadhar/PAN
    const ocrDiv = document.getElementById('ocr-' + docType);
    if (ocrDiv) {
        setTimeout(() => {
            if (docType === 'aadharcard') {
                ocrDiv.innerHTML = `<div class="alert alert-i" style="margin-top:8px;font-size:11px;"><span class="ai">🔍</span> OCR: Detected 12-digit number. Auto-filled below.</div>`;
                const f = document.querySelector('[name="AadharNumber"]');
                if (f && !f.value) f.value = '1234-5678-9012';
            } else if (docType === 'pancard') {
                ocrDiv.innerHTML = `<div class="alert alert-i" style="margin-top:8px;font-size:11px;"><span class="ai">🔍</span> OCR: PAN detected. Auto-filled.</div>`;
                const f = document.querySelector('[name="PANNumber"]');
                if (f && !f.value) f.value = 'ABCDE1234F';
            }
            ocrDiv.classList.remove('hidden');
        }, 800);
    }
}

// ===== DOCUMENT UPLOAD (AJAX) =====
function uploadDocument(requestId, docType, inputId, statusId) {
    const input = document.getElementById(inputId);
    const statusEl = document.getElementById(statusId);
    if (!input || !input.files[0]) {
        if (statusEl) statusEl.innerHTML = `<span style="color:#f44336">⚠ Please select a file first</span>`;
        return;
    }
    const formData = new FormData();
    formData.append('requestId', requestId);
    formData.append('documentType', docType);
    formData.append('file', input.files[0]);

    const token = document.querySelector('input[name="__RequestVerificationToken"]');
    if (token) formData.append('__RequestVerificationToken', token.value);

    if (statusEl) statusEl.innerHTML = `<span style="color:#f5a623">⏳ Uploading...</span>`;

    fetch('/SolarPanelUserPanel/SolarRequest/UploadDocument', { method: 'POST', body: formData })
        .then(r => r.json())
        .then(d => {
            if (statusEl) {
                statusEl.innerHTML = d.success
                    ? `<span style="color:#4caf50">✅ Uploaded successfully</span>`
                    : `<span style="color:#f44336">⚠ ${d.message}</span>`;
            }
        })
        .catch(() => {
            if (statusEl) statusEl.innerHTML = `<span style="color:#f44336">⚠ Upload failed</span>`;
        });
}

// ===== APPROVE/REJECT ADMIN =====
function approveRequest(id) {
    showConfirm('Approve this request?', () => {
        post('/SolarPanelAdmin/Projects/Approve', { id }, d => {
            if (d.success) { showSuccess(d.message || 'Approved!'); setTimeout(() => location.reload(), 1500); }
            else showError(d.message || 'Failed');
        });
    });
}

function rejectRequest(id) {
    if (typeof Swal !== 'undefined') {
        Swal.fire({
            title: 'Reject Request', input: 'text', inputLabel: 'Reason for rejection',
            inputPlaceholder: 'Enter reason...', showCancelButton: true,
            confirmButtonText: 'Reject', confirmButtonColor: '#f44336',
            background: '#ffffff', color: '#1f2937'
        }).then(r => {
            if (r.isConfirmed && r.value) {
                post('/SolarPanelAdmin/Projects/Reject', { id, reason: r.value }, d => {
                    if (d.success) { showSuccess('Rejected'); setTimeout(() => location.reload(), 1500); }
                    else showError(d.message || 'Failed');
                });
            }
        });
    } else {
        const reason = prompt('Reason for rejection:');
        if (reason) {
            post('/SolarPanelAdmin/Projects/Reject', { id, reason }, d => {
                if (d.success) { showSuccess('Rejected'); location.reload(); }
            });
        }
    }
}

// ===== AJAX POST HELPER =====
function post(url, data, cb) {
    const form = new FormData();
    Object.keys(data).forEach(k => form.append(k, data[k]));
    const token = document.querySelector('input[name="__RequestVerificationToken"]');
    if (token) form.append('__RequestVerificationToken', token.value);
    fetch(url, { method: 'POST', body: form })
        .then(r => r.json())
        .then(d => cb && cb(d))
        .catch(e => console.error(e));
}

// ===== PROGRESS BAR ANIMATION =====
function animateProgressBars() {
    document.querySelectorAll('.prog-bar[data-width]').forEach(bar => {
        const w = bar.dataset.width;
        setTimeout(() => { bar.style.width = w + '%'; }, 100);
    });
}

// ===== SIDEBAR TOGGLE (MOBILE) =====
function toggleSidebar() {
    const sb = document.querySelector('.sidebar');
    if (sb) sb.classList.toggle('open');
}

// ===== INIT =====
document.addEventListener('DOMContentLoaded', function () {
    animateProgressBars();

    // Auto-dismiss alerts
    document.querySelectorAll('.alert-s, .alert-e').forEach(a => {
        if (a.closest('.main-content')) {
            setTimeout(() => { a.style.opacity = '0'; a.style.transition = '.5s'; setTimeout(() => a.remove(), 500); }, 4000);
        }
    });

    // DataTable init if present
    if (typeof $.fn !== 'undefined' && typeof $.fn.DataTable !== 'undefined') {
        $('table.dt').DataTable({ pageLength: 20, language: { search: '🔍 Search:' } });
    }
});

// ===== PLAN AMOUNT UPDATE =====
function updatePlanAmount(kv) {
    const plans = { '1.1': 15900, '3': 19900, '5': 29900, '10': 49900 };
    const amt = plans[kv] || 15900;
    const display = document.getElementById('planAmountDisplay');
    if (display) display.textContent = '₹' + amt.toLocaleString('en-IN');
}

// ===== PAYMENT SUBMIT =====
function submitPayment(requestId) {
    const amt = document.getElementById('payAmt');
    const utr = document.getElementById('payUTR');
    const date = document.getElementById('payDate');
    const receipt = document.getElementById('payReceipt');

    if (!amt || !amt.value || !utr || !utr.value) {
        showError('Please fill amount and UTR number');
        return;
    }

    const formData = new FormData();
    formData.append('SolarRequestId', requestId);
    formData.append('Amount', amt.value);
    formData.append('UTRNumber', utr.value);
    formData.append('PaymentDate', date ? date.value : new Date().toISOString().split('T')[0]);
    if (receipt && receipt.files[0]) formData.append('receiptImage', receipt.files[0]);
    const token = document.querySelector('input[name="__RequestVerificationToken"]');
    if (token) formData.append('__RequestVerificationToken', token.value);

    fetch('/SolarPanelUserPanel/SolarRequest/AddPayment', { method: 'POST', body: formData })
        .then(r => r.json())
        .then(d => {
            if (d.success) { showSuccess('Payment submitted! Awaiting verification.'); setTimeout(() => location.reload(), 2000); }
            else showError(d.message || 'Failed to submit payment');
        })
        .catch(() => showError('Network error'));
}