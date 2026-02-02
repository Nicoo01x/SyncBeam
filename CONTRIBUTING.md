# Contribuir a SyncBeam

¡Gracias por tu interés en contribuir a SyncBeam! 🎉

## 📋 Tabla de Contenidos

- [Código de Conducta](#código-de-conducta)
- [Cómo Contribuir](#cómo-contribuir)
- [Reportar Bugs](#reportar-bugs)
- [Sugerir Features](#sugerir-features)
- [Pull Requests](#pull-requests)
- [Estilo de Código](#estilo-de-código)
- [Commits](#commits)

---

## 📜 Código de Conducta

Este proyecto sigue un código de conducta inclusivo y respetuoso. Al participar, se espera que:

- Uses lenguaje inclusivo y respetuoso
- Respetes diferentes puntos de vista y experiencias
- Aceptes críticas constructivas con gracia
- Te enfoques en lo mejor para la comunidad

---

## 🤝 Cómo Contribuir

### 1. Fork y Clone

```bash
# Fork el repo en GitHub, luego:
git clone https://github.com/TU-USUARIO/SyncBeam.git
cd SyncBeam
git remote add upstream https://github.com/ORIGINAL/SyncBeam.git
```

### 2. Crear Branch

```bash
git checkout -b feature/mi-nueva-feature
# o
git checkout -b fix/descripcion-del-bug
```

### 3. Hacer Cambios

- Escribe código limpio y documentado
- Añade tests si es aplicable
- Asegúrate de que compila sin errores ni warnings

### 4. Commit y Push

```bash
git add .
git commit -m "Add: descripción clara del cambio"
git push origin feature/mi-nueva-feature
```

### 5. Crear Pull Request

Abre un PR en GitHub con una descripción clara de tus cambios.

---

## 🐛 Reportar Bugs

Antes de reportar un bug:

1. **Busca** en los issues existentes
2. **Verifica** que estés usando la última versión
3. **Reproduce** el bug de forma consistente

Al crear el issue incluye:

- **Título claro**: Descripción breve del problema
- **Pasos para reproducir**: Paso a paso para replicar el bug
- **Comportamiento esperado**: Qué debería pasar
- **Comportamiento actual**: Qué pasa realmente
- **Entorno**: Windows version, .NET version, etc.
- **Logs/Screenshots**: Si aplica

```markdown
## Bug Report

**Descripción**
Breve descripción del bug.

**Pasos para reproducir**
1. Ir a '...'
2. Hacer click en '...'
3. Ver error

**Comportamiento esperado**
Descripción de lo que debería pasar.

**Screenshots**
Si aplica, añadir screenshots.

**Entorno**
- OS: Windows 11
- .NET: 8.0
- Version: 1.0.0
```

---

## 💡 Sugerir Features

¡Las ideas son bienvenidas! Al sugerir una feature:

1. **Verifica** que no exista ya un issue similar
2. **Describe** el problema que resuelve
3. **Proporciona** ejemplos de uso

```markdown
## Feature Request

**Problema**
Descripción del problema que esta feature resolvería.

**Solución propuesta**
Descripción de la solución que te gustaría.

**Alternativas consideradas**
Otras soluciones que consideraste.

**Contexto adicional**
Cualquier otro contexto o screenshots.
```

---

## 🔀 Pull Requests

### Proceso de Review

1. **Auto-review**: Revisa tu propio código antes de submitir
2. **CI/CD**: Asegúrate de que pasan todos los checks
3. **Review**: Un maintainer revisará tu PR
4. **Feedback**: Puede que se pidan cambios
5. **Merge**: Una vez aprobado, se hará merge

### Checklist del PR

- [ ] El código compila sin errores
- [ ] No hay warnings nuevos
- [ ] Se siguió el estilo de código del proyecto
- [ ] Se añadieron tests si aplica
- [ ] Se actualizó la documentación si aplica
- [ ] El commit message sigue la convención

### Template de PR

```markdown
## Descripción

Breve descripción de los cambios.

## Tipo de cambio

- [ ] Bug fix
- [ ] Nueva feature
- [ ] Breaking change
- [ ] Documentación

## Checklist

- [ ] Mi código sigue el estilo del proyecto
- [ ] He hecho self-review de mi código
- [ ] He comentado código complejo
- [ ] He actualizado la documentación
- [ ] Mis cambios no generan warnings
- [ ] He añadido tests

## Screenshots (si aplica)

Añadir screenshots de UI changes.
```

---

## 🎨 Estilo de Código

### C#

```csharp
// ✅ Bien
public async Task<Result> ProcessFileAsync(string filePath, CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(filePath);

    var result = await _service.ProcessAsync(filePath, ct);
    return result;
}

// ❌ Mal
public async Task<Result> processFile(string file_path, CancellationToken cancellationToken) {
    if (file_path == null) throw new ArgumentNullException();
    var result = await _service.ProcessAsync(file_path, cancellationToken);
    return result;
}
```

**Reglas generales:**

- Usar `PascalCase` para tipos y métodos públicos
- Usar `camelCase` para variables locales y parámetros
- Usar `_camelCase` para campos privados
- Preferir `var` cuando el tipo es obvio
- Usar expresiones de cuerpo para métodos simples
- Documentar métodos públicos con XML comments

### CSS

```css
/* ✅ Bien - usar variables CSS */
.button {
    background: var(--accent-primary);
    border-radius: var(--radius-md);
    transition: var(--transition-base);
}

/* ❌ Mal - valores hardcodeados */
.button {
    background: #6366f1;
    border-radius: 12px;
    transition: all 0.25s ease;
}
```

### JavaScript

```javascript
// ✅ Bien
const handlePeerConnection = async (peerId) => {
    try {
        await this.sendToBackend('connect', { peerId });
        this.showNotification('Connected!');
    } catch (error) {
        console.error('Connection failed:', error);
    }
};

// ❌ Mal
function handlePeerConnection(peerId) {
    this.sendToBackend('connect', { peerId: peerId }).then(function() {
        this.showNotification('Connected!');
    });
}
```

---

## 📝 Commits

### Formato

```
<tipo>: <descripción>

[cuerpo opcional]

[footer opcional]
```

### Tipos

| Tipo | Descripción |
|------|-------------|
| `Add` | Nueva feature |
| `Fix` | Bug fix |
| `Update` | Actualización de feature existente |
| `Remove` | Eliminación de código/feature |
| `Refactor` | Refactorización sin cambio funcional |
| `Docs` | Cambios en documentación |
| `Style` | Cambios de formato (no afectan lógica) |
| `Test` | Añadir o modificar tests |
| `Chore` | Tareas de mantenimiento |

### Ejemplos

```bash
# ✅ Buenos commits
git commit -m "Add: file transfer resume functionality"
git commit -m "Fix: mDNS discovery not finding peers on some networks"
git commit -m "Update: improve handshake timeout handling"
git commit -m "Docs: add API documentation for PeerManager"

# ❌ Malos commits
git commit -m "fix stuff"
git commit -m "WIP"
git commit -m "asdfasdf"
```

---

## 🏷️ Labels

| Label | Descripción |
|-------|-------------|
| `bug` | Algo no funciona correctamente |
| `enhancement` | Nueva feature o mejora |
| `documentation` | Mejoras en documentación |
| `good first issue` | Bueno para nuevos contribuidores |
| `help wanted` | Se necesita ayuda extra |
| `question` | Pregunta o discusión |
| `wontfix` | No se trabajará en esto |

---

## ❓ Preguntas

¿Tienes preguntas? Abre un [Discussion](https://github.com/yourusername/SyncBeam/discussions) o un issue con el label `question`.

---

<div align="center">

**¡Gracias por contribuir!** 🙏

</div>
