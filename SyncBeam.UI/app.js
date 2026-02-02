// SyncBeam UI Application

class SyncBeamApp {
    constructor() {
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
        this.setupNavigation();
        this.setupDropZone();
        this.setupEventListeners();
        this.setupSyncBeamBridge();

        // Request initial state
        setTimeout(() => {
            this.sendToBackend('getState', {});
        }, 500);
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

        if (!dropZone || !fileInput) return;

        dropZone.addEventListener('click', () => fileInput.click());

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
            const files = Array.from(e.dataTransfer.files);
            this.handleFiles(files);
        });

        fileInput.addEventListener('change', (e) => {
            const files = Array.from(e.target.files);
            this.handleFiles(files);
            fileInput.value = '';
        });
    }

    handleFiles(files) {
        files.forEach(file => {
            console.log('File selected:', file.name, file.size);
            // Will send to backend in Phase 2
            this.sendToBackend('sendFile', {
                name: file.name,
                size: file.size,
                type: file.type
            });
        });
    }

    setupEventListeners() {
        // Refresh button
        const refreshBtn = document.getElementById('refreshBtn');
        if (refreshBtn) {
            refreshBtn.addEventListener('click', () => {
                this.sendToBackend('refresh', {});
                this.showNotification('Scanning for peers...');
            });
        }

        // Clipboard sync toggle
        const clipboardSync = document.getElementById('clipboardSync');
        if (clipboardSync) {
            clipboardSync.addEventListener('change', (e) => {
                this.sendToBackend('setClipboardSync', { enabled: e.target.checked });
            });
        }
    }

    setupSyncBeamBridge() {
        // Listen for events from C# backend
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
                this.showNotification(`Connected to peer ${data.peerId.substring(0, 8)}...`);
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
        } else {
            console.log('Backend message:', action, data);
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
                    <div class="empty-icon">📡</div>
                    <h3>Scanning for peers...</h3>
                    <p>Make sure other SyncBeam instances are running on your network with the same secret.</p>
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
                                ${isConnected ? 'Connected' : 'Discovered'}
                            </div>
                        </div>
                    </div>
                    <div class="peer-endpoint" style="font-size: 12px; color: var(--text-muted); margin-bottom: 12px;">
                        ${peer.endpoint || 'Unknown'}
                    </div>
                    <div class="peer-card-actions">
                        ${isConnected ? `
                            <button class="btn btn-secondary" onclick="app.sendFileToPeer('${peer.peerId}')">
                                Send File
                            </button>
                        ` : `
                            <button class="btn btn-primary" onclick="app.connectToPeer('${peer.peerId}')">
                                Connect
                            </button>
                        `}
                    </div>
                </div>
            `;
        }).join('');
    }

    connectToPeer(peerId) {
        this.sendToBackend('connect', { peerId });
        this.showNotification(`Connecting to ${peerId.substring(0, 8)}...`);
    }

    sendFileToPeer(peerId) {
        const fileInput = document.getElementById('fileInput');
        if (fileInput) {
            fileInput.dataset.targetPeer = peerId;
            fileInput.click();
        }
    }

    addTransfer(transfer) {
        this.state.transfers.unshift(transfer);
        this.renderTransfers();
    }

    updateTransferProgress(data) {
        const transfer = this.state.transfers.find(t => t.id === data.transferId);
        if (transfer) {
            transfer.progress = data.progress;
            transfer.speed = data.speed;
            this.renderTransfers();
        }
    }

    renderTransfers() {
        const list = document.getElementById('transferList');
        if (!list) return;

        if (this.state.transfers.length === 0) {
            list.innerHTML = `
                <div class="empty-state small">
                    <p>No active transfers</p>
                </div>
            `;
            return;
        }

        list.innerHTML = this.state.transfers.map(transfer => `
            <div class="transfer-item">
                <div class="transfer-icon">${this.getFileIcon(transfer.type)}</div>
                <div class="transfer-info">
                    <div class="transfer-name">${transfer.name}</div>
                    <div class="transfer-meta">
                        ${this.formatSize(transfer.size)} • ${transfer.speed || '0 B/s'}
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

        // Keep only last 50 items
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
                    <p>Clipboard history will appear here</p>
                </div>
            `;
            return;
        }

        history.innerHTML = this.state.clipboardHistory.map(item => `
            <div class="clipboard-item" onclick="app.copyToClipboard('${item.id}')">
                <div class="clipboard-content">${this.escapeHtml(item.content?.substring(0, 200) || '')}</div>
                <div class="clipboard-meta">
                    <span>From: ${item.peerId?.substring(0, 8) || 'Local'}...</span>
                    <span>${this.formatTime(item.timestamp)}</span>
                </div>
            </div>
        `).join('');
    }

    copyToClipboard(itemId) {
        const item = this.state.clipboardHistory.find(i => i.id === parseInt(itemId));
        if (item && item.content) {
            navigator.clipboard.writeText(item.content);
            this.showNotification('Copied to clipboard');
        }
    }

    showNotification(message) {
        // Simple notification - could be enhanced with a toast system
        console.log('Notification:', message);
    }

    // Utility functions
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

    getFileIcon(type) {
        if (!type) return '📄';
        if (type.startsWith('image/')) return '🖼️';
        if (type.startsWith('video/')) return '🎬';
        if (type.startsWith('audio/')) return '🎵';
        if (type.includes('pdf')) return '📕';
        if (type.includes('zip') || type.includes('rar')) return '📦';
        return '📄';
    }

    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }
}

// Initialize the app
const app = new SyncBeamApp();
