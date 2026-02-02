<div align="center">

# ⚡ SyncBeam

### Transferencia P2P de archivos y portapapeles para Windows

[![License: MIT](https://img.shields.io/badge/License-MIT-6366f1.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows)](https://www.microsoft.com/windows)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-22c55e.svg)](CONTRIBUTING.md)

<p align="center">
  <strong>🔒 Sin servidores • 🌐 100% Local • ⚡ Ultra rápido</strong>
</p>

<img src="https://raw.githubusercontent.com/yourusername/SyncBeam/main/docs/screenshot.png" alt="SyncBeam Screenshot" width="800"/>

</div>

---

## ✨ Características

| Característica | Descripción |
|----------------|-------------|
| 🔍 **Auto-descubrimiento** | Encuentra automáticamente otros dispositivos SyncBeam en tu red via mDNS |
| 🔐 **Cifrado E2E** | Noise Protocol XX + AES-256-GCM para máxima seguridad |
| 📁 **Transferencia de archivos** | Soporte para archivos >10GB con reanudación automática |
| 📋 **Sync de portapapeles** | Texto, imágenes, RTF y HTML sincronizados en tiempo real |
| 🎯 **Drag & Drop** | Arrastra archivos al outbox para enviarlos automáticamente |
| 🎨 **UI Moderna** | Interfaz glassmorphism oscura con WebView2 |
| 🚫 **Sin cloud** | Cero servidores, cero tracking, 100% peer-to-peer |

---

## 🚀 Inicio Rápido

### Requisitos

- Windows 10/11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- WebView2 Runtime (incluido en Windows 10/11)

### Instalación

```bash
# Clonar el repositorio
git clone https://github.com/yourusername/SyncBeam.git
cd SyncBeam

# Restaurar dependencias
dotnet restore

# Compilar
dotnet build

# Ejecutar
dotnet run --project SyncBeam.App
```

### Uso Rápido

1. **Ejecuta SyncBeam** en dos o más PCs de la misma red
2. **Comparte el mismo secreto** (se genera automáticamente en `~/SyncBeam/.secret`)
3. **Conecta** haciendo clic en el peer descubierto
4. **Transfiere** arrastrando archivos o copiando al portapapeles

---

## 🏗️ Arquitectura

```
┌─────────────────────────────────────────────────────────────────┐
│                         SyncBeam.App                            │
│                    (WPF + WebView2 UI)                          │
├─────────────────────────────────────────────────────────────────┤
│  SyncBeam.Streams  │  SyncBeam.Clipboard  │    SyncBeam.UI     │
│  (File Transfer)   │  (Clipboard Sync)    │   (HTML/CSS/JS)    │
├─────────────────────────────────────────────────────────────────┤
│                         SyncBeam.P2P                            │
│  ┌──────────────┬──────────────┬──────────────┬──────────────┐ │
│  │   Discovery  │  Handshake   │  Transport   │ NatTraversal │ │
│  │    (mDNS)    │ (Noise XX)   │ (TCP+AES)    │   (STUN)     │ │
│  └──────────────┴──────────────┴──────────────┴──────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

### Estructura del Proyecto

```
SyncBeam/
├── 📁 SyncBeam.App/           # Aplicación WPF principal
│   ├── MainWindow.xaml        # Ventana con WebView2
│   └── WebViewHost.cs         # Bridge JS ↔ C#
│
├── 📁 SyncBeam.P2P/           # Librería de networking P2P
│   ├── Core/                  # Criptografía (Ed25519, AES-GCM)
│   ├── Discovery/             # mDNS para descubrimiento
│   ├── Handshake/             # Noise Protocol XX
│   ├── Transport/             # Transporte TCP seguro
│   ├── NatTraversal/          # STUN + hole punching
│   └── PeerManager.cs         # Gestión de peers
│
├── 📁 SyncBeam.Streams/       # Motor de transferencia
│   ├── FileTransferEngine.cs  # Chunked streaming + resume
│   └── OutboxWatcher.cs       # Auto-beam desde outbox
│
├── 📁 SyncBeam.Clipboard/     # Sincronización de portapapeles
│   └── ClipboardWatcher.cs    # Monitor + sync
│
├── 📁 SyncBeam.UI/            # Interfaz web
│   ├── index.html
│   ├── styles.css             # Glassmorphism UI
│   └── app.js
│
└── 📁 SyncBeam.Console/       # App de prueba CLI
    └── Program.cs
```

---

## 🔒 Seguridad

SyncBeam implementa seguridad de grado militar:

| Capa | Tecnología | Propósito |
|------|------------|-----------|
| **Identidad** | Ed25519 | Claves de firma únicas por dispositivo |
| **Handshake** | Noise Protocol XX | Autenticación mutua con ocultación de identidad |
| **Transporte** | AES-256-GCM | Cifrado autenticado de todos los datos |
| **Integridad** | SHA-256 | Verificación de cada chunk transferido |
| **Autorización** | Project Secret | Solo peers con el mismo secreto pueden conectar |

### Flujo de Handshake

```
    Iniciador                                    Respondedor
        │                                             │
        │──── e ─────────────────────────────────────►│  1. Envía clave efímera
        │                                             │
        │◄─── e, ee, s, es ──────────────────────────│  2. Intercambio DH + clave estática cifrada
        │                                             │
        │──── s, se ─────────────────────────────────►│  3. Clave estática + verificación
        │                                             │
        │◄─── ✓ ──────────────────────────────────────│  4. Canal seguro establecido
        │                                             │
```

---

## 📁 Directorios

| Directorio | Propósito |
|------------|-----------|
| `~/SyncBeam/inbox` | Archivos recibidos se guardan aquí |
| `~/SyncBeam/outbox` | Arrastra archivos aquí para enviarlos automáticamente |
| `~/SyncBeam/.secret` | Tu secreto de proyecto (compártelo con peers autorizados) |

---

## 🛠️ Desarrollo

### Compilar desde código

```bash
# Debug
dotnet build

# Release
dotnet build -c Release

# Publicar ejecutable independiente
dotnet publish -c Release -r win-x64 --self-contained
```

### Ejecutar tests

```bash
# Consola de prueba P2P
dotnet run --project SyncBeam.Console "mi-secreto"

# En otra terminal con el mismo secreto
dotnet run --project SyncBeam.Console "mi-secreto"
```

### Comandos de la consola de prueba

| Comando | Descripción |
|---------|-------------|
| `list` | Lista peers descubiertos |
| `connect` | Conectar a un peer |
| `peers` | Mostrar peers conectados |
| `send` | Enviar mensaje de prueba |
| `ping` | Ping a todos los peers |
| `refresh` | Refrescar descubrimiento |
| `quit` | Salir |

---

## 🤝 Contribuir

¡Las contribuciones son bienvenidas! Por favor lee [CONTRIBUTING.md](CONTRIBUTING.md) para detalles.

1. Fork el repositorio
2. Crea tu feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit tus cambios (`git commit -m 'Add: AmazingFeature'`)
4. Push al branch (`git push origin feature/AmazingFeature`)
5. Abre un Pull Request

---

## 📜 Licencia

Este proyecto está licenciado bajo la Licencia MIT - ver [LICENSE](LICENSE) para detalles.

---

## 🙏 Agradecimientos

- [Noise Protocol](https://noiseprotocol.org/) - Framework de cifrado
- [Makaretu.Dns](https://github.com/richardschneider/net-mdns) - mDNS para .NET
- [NSec](https://nsec.rocks/) - Criptografía moderna para .NET
- [MessagePack](https://msgpack.org/) - Serialización binaria eficiente

---

<div align="center">

**Hecho con ❤️ para la comunidad**

[⬆ Volver arriba](#-syncbeam)

</div>
