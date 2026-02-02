// SyncBeam UI Application

// Internationalization
const i18n = {
    en: {
        initializing: 'Initializing...',
        'nav.peers': 'Peers',
        'nav.transfers': 'Transfers',
        'nav.clipboard': 'Clipboard',
        'nav.settings': 'Settings',
        'peers.title': 'Nearby Peers',
        'peers.refresh': 'Refresh',
        'peers.scanning': 'Scanning for peers...',
        'peers.scanningHint': 'Make sure other SyncBeam instances are running on your network with the same secret.',
        'peers.connect': 'Connect',
        'peers.sendFile': 'Send File',
        'peers.discovered': 'Discovered',
        'peers.connected': 'Connected',
        'transfers.title': 'File Transfers',
        'transfers.dropTitle': 'Drop files here to send',
        'transfers.dropHint': 'or click to browse',
        'transfers.selectFiles': 'Select Files',
        'transfers.selectFolder': 'Select Folder',
        'transfers.noActive': 'No active transfers',
        'transfers.cancel': 'Cancel',
        'transfers.status.pending': 'Waiting...',
        'transfers.status.sending': 'Sending',
        'transfers.status.receiving': 'Receiving',
        'transfers.status.completed': 'Completed',
        'transfers.status.failed': 'Failed',
        'clipboard.title': 'Clipboard Sync',
        'clipboard.autoSync': 'Auto-sync',
        'clipboard.noHistory': 'Clipboard history will appear here',
        'clipboard.from': 'From',
        'settings.title': 'Settings',
        'settings.language': 'Language',
        'settings.network': 'Network',
        'settings.projectSecret': 'Project Secret',
        'settings.secretHint': 'Peers must share the same secret to connect.',
        'settings.listenPort': 'Listen Port',
        'settings.storage': 'Storage',
        'settings.inboxDir': 'Inbox Directory',
        'settings.outboxDir': 'Outbox Directory',
        'settings.about': 'About',
        'settings.version': 'Version',
        'settings.copySecret': 'Copy',
        'settings.saveSecret': 'Save',
        'settings.copied': 'Copied!',
        'settings.connectHint': 'Share this secret with another PC to connect',
        'settings.secretSaved': 'Secret saved! Reconnecting...'
    },
    es: {
        initializing: 'Iniciando...',
        'nav.peers': 'Dispositivos',
        'nav.transfers': 'Transferencias',
        'nav.clipboard': 'Portapapeles',
        'nav.settings': 'Configuración',
        'peers.title': 'Dispositivos Cercanos',
        'peers.refresh': 'Actualizar',
        'peers.scanning': 'Buscando dispositivos...',
        'peers.scanningHint': 'Asegúrate de que otras instancias de SyncBeam estén ejecutándose en tu red con el mismo secreto.',
        'peers.connect': 'Conectar',
        'peers.sendFile': 'Enviar Archivo',
        'peers.discovered': 'Descubierto',
        'peers.connected': 'Conectado',
        'transfers.title': 'Transferencias',
        'transfers.dropTitle': 'Arrastra archivos aquí para enviar',
        'transfers.dropHint': 'o haz clic para buscar',
        'transfers.selectFiles': 'Seleccionar Archivos',
        'transfers.selectFolder': 'Seleccionar Carpeta',
        'transfers.noActive': 'No hay transferencias activas',
        'transfers.cancel': 'Cancelar',
        'transfers.status.pending': 'Esperando...',
        'transfers.status.sending': 'Enviando',
        'transfers.status.receiving': 'Recibiendo',
        'transfers.status.completed': 'Completado',
        'transfers.status.failed': 'Fallido',
        'clipboard.title': 'Sincronización de Portapapeles',
        'clipboard.autoSync': 'Auto-sincronizar',
        'clipboard.noHistory': 'El historial del portapapeles aparecerá aquí',
        'clipboard.from': 'De',
        'settings.title': 'Configuración',
        'settings.language': 'Idioma',
        'settings.network': 'Red',
        'settings.projectSecret': 'Secreto del Proyecto',
        'settings.secretHint': 'Los dispositivos deben compartir el mismo secreto para conectarse.',
        'settings.listenPort': 'Puerto de Escucha',
        'settings.storage': 'Almacenamiento',
        'settings.inboxDir': 'Directorio de Entrada',
        'settings.outboxDir': 'Directorio de Salida',
        'settings.about': 'Acerca de',
        'settings.version': 'Versión',
        'settings.copySecret': 'Copiar',
        'settings.saveSecret': 'Guardar',
        'settings.copied': 'Copiado!',
        'settings.connectHint': 'Comparte este secreto con otra PC para conectarte',
        'settings.secretSaved': 'Secreto guardado! Reconectando...'
    }
};

class SyncBeamApp {
    constructor() {
        this.currentLang = localStorage.getItem('syncbeam-lang') || 'en';
        this.state = {
            localPeerId: null,
            listenPort: null,
            projectSecret: null,
            discoveredPeers: new Map(),
            connectedPeers: new Map(),
            transfers: [],
            clipboardHistory: []
        };

        this.init();
    }

    init() {
        this.applyLanguage(this.currentLang);
        this.setupNavigation();
        this.setupDropZone();
        this.setupEventListeners();
        this.setupLanguageSelector();
        this.setupSyncBeamBridge();

        setTimeout(() => {
            this.sendToBackend('getState', {});
        }, 500);
    }

    // i18n
    t(key) {
        return i18n[this.currentLang][key] || i18n['en'][key] || key;
    }

    applyLanguage(lang) {
        this.currentLang = lang;
        localStorage.setItem('syncbeam-lang', lang);
        document.documentElement.lang = lang;

        document.querySelectorAll('[data-i18n]').forEach(el => {
            const key = el.getAttribute('data-i18n');
            el.textContent = this.t(key);
        });

        document.querySelectorAll('.lang-btn').forEach(btn => {
            btn.classList.toggle('active', btn.dataset.lang === lang);
        });

        this.renderPeers();
    }

    setupLanguageSelector() {
        document.querySelectorAll('.lang-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                this.applyLanguage(btn.dataset.lang);
            });
        });
    }

    setupNavigation() {
        const navItems = document.querySelectorAll('.nav-item[data-view]');
        navItems.forEach(item => {
            item.addEventListener('click', () => {
                const viewId = item.dataset.view;
                this.switchView(viewId);

                navItems.forEach(n => n.classList.remove('active'));
                item.classList.add('active');
            });
        });
    }

    switchView(viewId) {
        document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
        const view = document.getElementById(`${viewId}View`);
        if (view) {
            view.classList.add('active');
        }
    }

    setupDropZone() {
        const dropZone = document.getElementById('dropZone');
        const fileInput = document.getElementById('fileInput');
        const folderInput = document.getElementById('folderInput');
        const selectFilesBtn = document.getElementById('selectFilesBtn');
        const selectFolderBtn = document.getElementById('selectFolderBtn');

        if (!dropZone || !fileInput) return;

        // File selection button
        if (selectFilesBtn) {
            selectFilesBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                fileInput.click();
            });
        }

        // Folder selection button
        if (selectFolderBtn && folderInput) {
            selectFolderBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                folderInput.click();
            });
        }

        dropZone.addEventListener('dragover', (e) => {
            e.preventDefault();
            dropZone.classList.add('dragover');
        });

        dropZone.addEventListener('dragleave', () => {
            dropZone.classList.remove('dragover');
        });

        dropZone.addEventListener('drop', (e) => {
            e.preventDefault();
            dropZone.classList.remove('dragover');

            // Handle dropped items (files and folders)
            const items = e.dataTransfer.items;
            if (items) {
                this.handleDroppedItems(items);
            } else {
                const files = Array.from(e.dataTransfer.files);
                this.handleFiles(files);
            }
        });

        fileInput.addEventListener('change', (e) => {
            const files = Array.from(e.target.files);
            this.handleFiles(files);
            fileInput.value = '';
        });

        // Folder input
        if (folderInput) {
            folderInput.addEventListener('change', (e) => {
                const files = Array.from(e.target.files);
                this.handleFilesInBatches(files, true);
                folderInput.value = '';
            });
        }
    }

    async handleFilesInBatches(files, preservePath = false) {
        const MAX_FILES_PER_BATCH = 50;
        const MAX_TOTAL_FILES = 1000;

        const totalToProcess = Math.min(files.length, MAX_TOTAL_FILES);

        for (let i = 0; i < totalToProcess; i += MAX_FILES_PER_BATCH) {
            const batch = files.slice(i, i + MAX_FILES_PER_BATCH);
            this.handleFiles(batch, preservePath);

            // Small delay to let UI update
            if (i + MAX_FILES_PER_BATCH < totalToProcess) {
                await new Promise(r => setTimeout(r, 10));
            }
        }

        if (files.length > MAX_TOTAL_FILES) {
            console.warn(`File limit reached (${MAX_TOTAL_FILES}). ${files.length - MAX_TOTAL_FILES} files were not included.`);
        }
    }

    async handleDroppedItems(items) {
        const entries = [];

        for (const item of items) {
            if (item.kind === 'file') {
                const entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null;
                if (entry) {
                    entries.push(entry);
                }
            }
        }

        // Process entries in batches
        this.processEntriesInBatches(entries);
    }

    async processEntriesInBatches(entries) {
        const MAX_FILES_PER_BATCH = 50;
        const MAX_TOTAL_FILES = 1000;
        let totalFiles = 0;
        let batch = [];

        const processFile = async (entry, path) => {
            if (totalFiles >= MAX_TOTAL_FILES) {
                return false;
            }

            try {
                const file = await new Promise((resolve, reject) => {
                    entry.file(resolve, reject);
                });
                file.relativePath = path + file.name;
                batch.push(file);
                totalFiles++;

                if (batch.length >= MAX_FILES_PER_BATCH) {
                    this.handleFiles([...batch], true);
                    batch = [];
                    // Small delay to let UI breathe
                    await new Promise(r => setTimeout(r, 10));
                }
                return true;
            } catch (e) {
                console.warn('Could not read file:', path + entry.name);
                return true;
            }
        };

        const processDirectory = async (dirEntry, path) => {
            if (totalFiles >= MAX_TOTAL_FILES) {
                return;
            }

            const reader = dirEntry.createReader();
            let allEntries = [];

            // readEntries may not return all entries at once
            const readAllEntries = async () => {
                const entries = await new Promise((resolve, reject) => {
                    reader.readEntries(resolve, reject);
                });
                if (entries.length > 0) {
                    allEntries = allEntries.concat(entries);
                    await readAllEntries();
                }
            };

            try {
                await readAllEntries();
            } catch (e) {
                console.warn('Could not read directory:', path);
                return;
            }

            for (const subEntry of allEntries) {
                if (totalFiles >= MAX_TOTAL_FILES) break;

                if (subEntry.isFile) {
                    await processFile(subEntry, path + dirEntry.name + '/');
                } else if (subEntry.isDirectory) {
                    await processDirectory(subEntry, path + dirEntry.name + '/');
                }
            }
        };

        // Process all entries
        for (const entry of entries) {
            if (totalFiles >= MAX_TOTAL_FILES) break;

            if (entry.isFile) {
                await processFile(entry, '');
            } else if (entry.isDirectory) {
                await processDirectory(entry, '');
            }
        }

        // Send remaining batch
        if (batch.length > 0) {
            this.handleFiles(batch, true);
        }

        // Warn if limit reached
        if (totalFiles >= MAX_TOTAL_FILES) {
            console.warn(`File limit reached (${MAX_TOTAL_FILES}). Some files may not be included.`);
        }
    }

    async processEntry(entry, files, path = '') {
        // Kept for compatibility but not used for drag & drop anymore
        if (entry.isFile) {
            try {
                const file = await new Promise((resolve, reject) => entry.file(resolve, reject));
                file.relativePath = path + file.name;
                files.push(file);
            } catch (e) {
                console.warn('Could not read file:', path + entry.name);
            }
        } else if (entry.isDirectory) {
            const reader = entry.createReader();
            try {
                const entries = await new Promise((resolve, reject) => {
                    reader.readEntries(resolve, reject);
                });
                for (const subEntry of entries) {
                    await this.processEntry(subEntry, files, path + entry.name + '/');
                }
            } catch (e) {
                console.warn('Could not read directory:', path + entry.name);
            }
        }
    }

    handleFiles(files, preservePath = false) {
        const MAX_VISIBLE_TRANSFERS = 100;

        files.forEach(file => {
            const transferId = `transfer-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
            const filePath = preservePath ? (file.relativePath || file.webkitRelativePath || file.name) : file.name;

            // Add to transfers list immediately
            this.state.transfers.push({
                id: transferId,
                name: filePath,
                size: file.size,
                type: file.type,
                progress: 0,
                speed: null,
                status: 'pending'
            });

            this.sendToBackend('sendFile', {
                transferId: transferId,
                name: file.name,
                path: filePath,
                size: file.size,
                type: file.type
            });
        });

        // Limit visible transfers to prevent UI slowdown
        if (this.state.transfers.length > MAX_VISIBLE_TRANSFERS) {
            // Keep only the most recent transfers
            this.state.transfers = this.state.transfers.slice(-MAX_VISIBLE_TRANSFERS);
        }

        // Update UI (debounced)
        this.scheduleRenderTransfers();
    }

    scheduleRenderTransfers() {
        if (this._renderTimeout) {
            clearTimeout(this._renderTimeout);
        }
        this._renderTimeout = setTimeout(() => {
            this.renderTransfers();
            this._renderTimeout = null;
        }, 50);
    }

    setupEventListeners() {
        const refreshBtn = document.getElementById('refreshBtn');
        if (refreshBtn) {
            refreshBtn.addEventListener('click', () => {
                this.sendToBackend('refresh', {});
            });
        }

        const clipboardSync = document.getElementById('clipboardSync');
        if (clipboardSync) {
            clipboardSync.addEventListener('change', (e) => {
                this.sendToBackend('setClipboardSync', { enabled: e.target.checked });
            });
        }
    }

    setupSyncBeamBridge() {
        window.addEventListener('syncbeam', (e) => {
            const { event, data } = e.detail;
            this.handleBackendEvent(event, data);
        });
    }

    handleBackendEvent(event, data) {
        switch (event) {
            case 'initialized':
                this.state.localPeerId = data.localPeerId;
                this.state.listenPort = data.listenPort;
                this.state.projectSecret = data.projectSecret;
                this.updateLocalPeerInfo();
                this.updateSettingsInfo();
                break;

            case 'state':
                this.state.localPeerId = data.localPeerId;
                this.state.listenPort = data.listenPort;
                this.state.projectSecret = data.projectSecret;
                data.connectedPeers.forEach(p => {
                    this.state.connectedPeers.set(p.peerId, p);
                });
                this.updateLocalPeerInfo();
                this.updateSettingsInfo();
                this.renderPeers();
                break;

            case 'peerDiscovered':
                this.state.discoveredPeers.set(data.peerId, {
                    peerId: data.peerId,
                    endpoint: data.endpoint,
                    connected: false
                });
                this.renderPeers();
                break;

            case 'peerConnected':
                this.state.connectedPeers.set(data.peerId, {
                    peerId: data.peerId,
                    isIncoming: data.isIncoming
                });
                const discovered = this.state.discoveredPeers.get(data.peerId);
                if (discovered) {
                    discovered.connected = true;
                }
                this.renderPeers();
                break;

            case 'peerDisconnected':
                this.state.connectedPeers.delete(data.peerId);
                const disc = this.state.discoveredPeers.get(data.peerId);
                if (disc) {
                    disc.connected = false;
                }
                this.renderPeers();
                break;

            case 'transferProgress':
                this.updateTransferProgress(data);
                break;

            case 'transferStarted':
                this.updateTransferStatus(data.transferId, 'sending');
                break;

            case 'transferCompleted':
                this.updateTransferStatus(data.transferId, 'completed', 100);
                // Remove after 3 seconds
                setTimeout(() => {
                    this.removeTransfer(data.transferId);
                }, 3000);
                break;

            case 'transferFailed':
                this.updateTransferStatus(data.transferId, 'failed');
                break;

            case 'transferReceiving':
                // Incoming transfer from another peer
                this.addIncomingTransfer(data);
                break;

            case 'clipboardReceived':
                this.addClipboardItem(data);
                break;

            case 'secretChanged':
                this.state.localPeerId = data.localPeerId;
                this.state.listenPort = data.listenPort;
                this.state.projectSecret = data.projectSecret;
                this.state.discoveredPeers.clear();
                this.state.connectedPeers.clear();
                this.updateLocalPeerInfo();
                this.updateSettingsInfo();
                this.renderPeers();
                this.showNotification(this.t('settings.secretSaved'));
                break;
        }
    }

    showNotification(message) {
        // Simple notification - could be improved with a toast system
        const notification = document.createElement('div');
        notification.className = 'notification';
        notification.textContent = message;
        document.body.appendChild(notification);

        setTimeout(() => {
            notification.classList.add('show');
        }, 10);

        setTimeout(() => {
            notification.classList.remove('show');
            setTimeout(() => notification.remove(), 300);
        }, 3000);
    }

    sendToBackend(action, data) {
        if (window.SyncBeam) {
            window.SyncBeam.send(action, data);
        } else if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ action, data }));
        }
    }

    updateLocalPeerInfo() {
        const localPeerInfo = document.getElementById('localPeerInfo');
        if (localPeerInfo && this.state.localPeerId) {
            localPeerInfo.innerHTML = `
                <span class="status-dot"></span>
                <span class="peer-id">${this.state.localPeerId.substring(0, 12)}...</span>
            `;
        }

        const listenPort = document.getElementById('listenPort');
        if (listenPort && this.state.listenPort) {
            listenPort.textContent = this.state.listenPort;
        }
    }

    updateSettingsInfo() {
        const secretInput = document.getElementById('projectSecret');
        if (secretInput && this.state.projectSecret) {
            secretInput.value = this.state.projectSecret;
        }
    }

    copySecret() {
        const secretInput = document.getElementById('projectSecret');
        if (secretInput) {
            navigator.clipboard.writeText(secretInput.value);
            // Show brief feedback
            const btn = document.getElementById('copySecretBtn');
            if (btn) {
                const originalText = btn.textContent;
                btn.textContent = this.t('settings.copied');
                setTimeout(() => {
                    btn.textContent = originalText;
                }, 2000);
            }
        }
    }

    saveSecret() {
        const secretInput = document.getElementById('projectSecret');
        if (secretInput && secretInput.value.trim()) {
            this.sendToBackend('setSecret', { secret: secretInput.value.trim() });
        }
    }

    renderPeers() {
        const grid = document.getElementById('peersGrid');
        if (!grid) return;

        const allPeers = new Map([...this.state.discoveredPeers]);

        if (allPeers.size === 0) {
            grid.innerHTML = `
                <div class="empty-state">
                    <svg class="empty-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5">
                        <circle cx="12" cy="12" r="10"/>
                        <path d="M12 6v6l4 2"/>
                        <path d="M2 12h2M20 12h2M12 2v2M12 20v2"/>
                    </svg>
                    <h3>${this.t('peers.scanning')}</h3>
                    <p>${this.t('peers.scanningHint')}</p>
                </div>
            `;
            return;
        }

        grid.innerHTML = Array.from(allPeers.values()).map(peer => {
            const isConnected = this.state.connectedPeers.has(peer.peerId);
            const shortId = peer.peerId.substring(0, 8);
            const initials = shortId.substring(0, 2).toUpperCase();

            return `
                <div class="peer-card ${isConnected ? 'connected' : ''}" data-peer-id="${peer.peerId}">
                    <div class="peer-card-header">
                        <div class="peer-avatar">${initials}</div>
                        <div class="peer-info">
                            <div class="peer-name">Peer ${shortId}</div>
                            <div class="peer-status">
                                <span class="peer-status-dot ${isConnected ? 'online' : ''}"></span>
                                ${isConnected ? this.t('peers.connected') : this.t('peers.discovered')}
                            </div>
                        </div>
                    </div>
                    <div class="peer-endpoint">${peer.endpoint || 'Unknown'}</div>
                    <div class="peer-card-actions">
                        ${isConnected ? `
                            <button class="btn btn-secondary" onclick="app.sendFileToPeer('${peer.peerId}')">
                                ${this.t('peers.sendFile')}
                            </button>
                        ` : `
                            <button class="btn btn-primary" onclick="app.connectToPeer('${peer.peerId}')">
                                ${this.t('peers.connect')}
                            </button>
                        `}
                    </div>
                </div>
            `;
        }).join('');
    }

    connectToPeer(peerId) {
        this.sendToBackend('connect', { peerId });
    }

    sendFileToPeer(peerId) {
        const fileInput = document.getElementById('fileInput');
        if (fileInput) {
            fileInput.dataset.targetPeer = peerId;
            fileInput.click();
        }
    }

    updateTransferProgress(data) {
        let transfer = this.state.transfers.find(t => t.id === data.transferId);
        if (!transfer && data.transferId) {
            // Transfer started from backend (e.g., incoming)
            transfer = {
                id: data.transferId,
                name: data.name || 'Unknown',
                size: data.size || 0,
                progress: 0,
                speed: null,
                status: 'receiving'
            };
            this.state.transfers.push(transfer);
        }
        if (transfer) {
            transfer.progress = data.progress;
            transfer.speed = data.speed;
            if (data.status) transfer.status = data.status;
            this.scheduleRenderTransfers();
        }
    }

    updateTransferStatus(transferId, status, progress = null) {
        const transfer = this.state.transfers.find(t => t.id === transferId);
        if (transfer) {
            transfer.status = status;
            if (progress !== null) transfer.progress = progress;
            this.scheduleRenderTransfers();
        }
    }

    removeTransfer(transferId) {
        this.state.transfers = this.state.transfers.filter(t => t.id !== transferId);
        this.scheduleRenderTransfers();
    }

    addIncomingTransfer(data) {
        const existing = this.state.transfers.find(t => t.id === data.transferId);
        if (!existing) {
            this.state.transfers.push({
                id: data.transferId,
                name: data.name,
                size: data.size,
                progress: 0,
                speed: null,
                status: 'receiving',
                peerId: data.peerId
            });
            this.scheduleRenderTransfers();
        }
    }

    renderTransfers() {
        const list = document.getElementById('transferList');
        if (!list) return;

        if (this.state.transfers.length === 0) {
            list.innerHTML = `
                <div class="empty-state small">
                    <p>${this.t('transfers.noActive')}</p>
                </div>
            `;
            return;
        }

        list.innerHTML = this.state.transfers.map(transfer => {
            const statusClass = transfer.status || 'pending';
            const statusText = this.t(`transfers.status.${transfer.status}`) || transfer.status;
            const speedText = transfer.speed ? this.formatSpeed(transfer.speed) : '';
            const isFolder = transfer.name.includes('/');

            return `
                <div class="transfer-item ${statusClass}" data-transfer-id="${transfer.id}">
                    <div class="transfer-icon">
                        ${isFolder ? `
                            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
                            </svg>
                        ` : `
                            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
                                <polyline points="14,2 14,8 20,8"/>
                            </svg>
                        `}
                    </div>
                    <div class="transfer-info">
                        <div class="transfer-name">${this.escapeHtml(transfer.name)}</div>
                        <div class="transfer-meta">
                            <span>${this.formatSize(transfer.size)}</span>
                            ${speedText ? `<span class="transfer-speed">${speedText}</span>` : ''}
                            <span class="transfer-status">${statusText}</span>
                        </div>
                        <div class="transfer-progress">
                            <div class="transfer-progress-bar" style="width: ${transfer.progress || 0}%"></div>
                        </div>
                    </div>
                    <button class="transfer-cancel" onclick="app.cancelTransfer('${transfer.id}')" title="${this.t('transfers.cancel')}">
                        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <line x1="18" y1="6" x2="6" y2="18"/>
                            <line x1="6" y1="6" x2="18" y2="18"/>
                        </svg>
                    </button>
                </div>
            `;
        }).join('');
    }

    cancelTransfer(transferId) {
        this.sendToBackend('cancelTransfer', { transferId });
        this.removeTransfer(transferId);
    }

    formatSpeed(bytesPerSecond) {
        if (!bytesPerSecond) return '';
        return this.formatSize(bytesPerSecond) + '/s';
    }

    addClipboardItem(data) {
        this.state.clipboardHistory.unshift({
            id: Date.now(),
            content: data.content,
            type: data.type,
            peerId: data.peerId,
            timestamp: new Date()
        });

        if (this.state.clipboardHistory.length > 50) {
            this.state.clipboardHistory = this.state.clipboardHistory.slice(0, 50);
        }

        this.renderClipboard();
    }

    renderClipboard() {
        const history = document.getElementById('clipboardHistory');
        if (!history) return;

        if (this.state.clipboardHistory.length === 0) {
            history.innerHTML = `
                <div class="empty-state small">
                    <p>${this.t('clipboard.noHistory')}</p>
                </div>
            `;
            return;
        }

        history.innerHTML = this.state.clipboardHistory.map(item => `
            <div class="clipboard-item" onclick="app.copyToClipboard('${item.id}')">
                <div class="clipboard-content">${this.escapeHtml(item.content?.substring(0, 200) || '')}</div>
                <div class="clipboard-meta">
                    <span>${this.t('clipboard.from')}: ${item.peerId?.substring(0, 8) || 'Local'}...</span>
                    <span>${this.formatTime(item.timestamp)}</span>
                </div>
            </div>
        `).join('');
    }

    copyToClipboard(itemId) {
        const item = this.state.clipboardHistory.find(i => i.id === parseInt(itemId));
        if (item && item.content) {
            navigator.clipboard.writeText(item.content);
        }
    }

    formatSize(bytes) {
        if (!bytes) return '0 B';
        const units = ['B', 'KB', 'MB', 'GB', 'TB'];
        let i = 0;
        while (bytes >= 1024 && i < units.length - 1) {
            bytes /= 1024;
            i++;
        }
        return `${bytes.toFixed(1)} ${units[i]}`;
    }

    formatTime(date) {
        if (!date) return '';
        const d = new Date(date);
        return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }

    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }
}

const app = new SyncBeamApp();
