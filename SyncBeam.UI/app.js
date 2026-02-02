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
        'settings.version': 'Version'
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
        'settings.version': 'Versión'
    }
};

class SyncBeamApp {
    constructor() {
        this.currentLang = localStorage.getItem('syncbeam-lang') || 'en';
        this.state = {
            localPeerId: null,
            listenPort: null,
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
                this.handleFiles(files, true);
                folderInput.value = '';
            });
        }
    }

    async handleDroppedItems(items) {
        const files = [];
        const entries = [];

        for (const item of items) {
            if (item.kind === 'file') {
                const entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null;
                if (entry) {
                    entries.push(entry);
                } else {
                    const file = item.getAsFile();
                    if (file) files.push(file);
                }
            }
        }

        // Process entries (files and directories)
        for (const entry of entries) {
            await this.processEntry(entry, files);
        }

        if (files.length > 0) {
            this.handleFiles(files, true);
        }
    }

    async processEntry(entry, files, path = '') {
        if (entry.isFile) {
            const file = await new Promise((resolve) => entry.file(resolve));
            // Add relative path info
            file.relativePath = path + file.name;
            files.push(file);
        } else if (entry.isDirectory) {
            const reader = entry.createReader();
            const entries = await new Promise((resolve) => {
                reader.readEntries(resolve);
            });
            for (const subEntry of entries) {
                await this.processEntry(subEntry, files, path + entry.name + '/');
            }
        }
    }

    handleFiles(files, preservePath = false) {
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

        // Update UI
        this.renderTransfers();
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
                this.updateLocalPeerInfo();
                break;

            case 'state':
                this.state.localPeerId = data.localPeerId;
                this.state.listenPort = data.listenPort;
                data.connectedPeers.forEach(p => {
                    this.state.connectedPeers.set(p.peerId, p);
                });
                this.updateLocalPeerInfo();
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
        }
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
            this.renderTransfers();
        }
    }

    updateTransferStatus(transferId, status, progress = null) {
        const transfer = this.state.transfers.find(t => t.id === transferId);
        if (transfer) {
            transfer.status = status;
            if (progress !== null) transfer.progress = progress;
            this.renderTransfers();
        }
    }

    removeTransfer(transferId) {
        this.state.transfers = this.state.transfers.filter(t => t.id !== transferId);
        this.renderTransfers();
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
            this.renderTransfers();
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

        list.innerHTML = this.state.transfers.map(transfer => `
            <div class="transfer-item">
                <div class="transfer-icon">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
                        <polyline points="14,2 14,8 20,8"/>
                    </svg>
                </div>
                <div class="transfer-info">
                    <div class="transfer-name">${transfer.name}</div>
                    <div class="transfer-meta">
                        ${this.formatSize(transfer.size)} - ${transfer.speed || '0 B/s'}
                    </div>
                    <div class="transfer-progress">
                        <div class="transfer-progress-bar" style="width: ${transfer.progress || 0}%"></div>
                    </div>
                </div>
            </div>
        `).join('');
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
