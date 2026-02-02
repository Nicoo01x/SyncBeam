# Contributing to SyncBeam

Thank you for your interest in contributing to SyncBeam!

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [How to Contribute](#how-to-contribute)
- [Reporting Bugs](#reporting-bugs)
- [Suggesting Features](#suggesting-features)
- [Pull Requests](#pull-requests)
- [Code Style](#code-style)
- [Commits](#commits)

---

## Code of Conduct

This project follows an inclusive and respectful code of conduct. When participating, you are expected to:

- Use inclusive and respectful language
- Respect different viewpoints and experiences
- Accept constructive criticism gracefully
- Focus on what's best for the community

---

## How to Contribute

### 1. Fork and Clone

```bash
# Fork the repo on GitHub, then:
git clone https://github.com/YOUR-USERNAME/SyncBeam.git
cd SyncBeam
git remote add upstream https://github.com/Nicoo01x/SyncBeam.git
```

### 2. Create a Branch

```bash
git checkout -b feature/my-new-feature
# or
git checkout -b fix/bug-description
```

### 3. Make Changes

- Write clean and documented code
- Add tests if applicable
- Make sure it compiles without errors or warnings

### 4. Commit and Push

```bash
git add .
git commit -m "Add: clear description of the change"
git push origin feature/my-new-feature
```

### 5. Create Pull Request

Open a PR on GitHub with a clear description of your changes.

---

## Reporting Bugs

Before reporting a bug:

1. **Search** existing issues
2. **Verify** you're using the latest version
3. **Reproduce** the bug consistently

When creating the issue include:

- **Clear title**: Brief description of the problem
- **Steps to reproduce**: Step by step to replicate the bug
- **Expected behavior**: What should happen
- **Actual behavior**: What actually happens
- **Environment**: Windows version, .NET version, etc.
- **Logs/Screenshots**: If applicable

```markdown
## Bug Report

**Description**
Brief description of the bug.

**Steps to reproduce**
1. Go to '...'
2. Click on '...'
3. See error

**Expected behavior**
Description of what should happen.

**Screenshots**
If applicable, add screenshots.

**Environment**
- OS: Windows 11
- .NET: 8.0
- Version: 1.0.0
```

---

## Suggesting Features

Ideas are welcome! When suggesting a feature:

1. **Verify** a similar issue doesn't already exist
2. **Describe** the problem it solves
3. **Provide** usage examples

```markdown
## Feature Request

**Problem**
Description of the problem this feature would solve.

**Proposed solution**
Description of the solution you'd like.

**Alternatives considered**
Other solutions you considered.

**Additional context**
Any other context or screenshots.
```

---

## Pull Requests

### Review Process

1. **Self-review**: Review your own code before submitting
2. **CI/CD**: Make sure all checks pass
3. **Review**: A maintainer will review your PR
4. **Feedback**: Changes may be requested
5. **Merge**: Once approved, it will be merged

### PR Checklist

- [ ] Code compiles without errors
- [ ] No new warnings
- [ ] Project code style was followed
- [ ] Tests were added if applicable
- [ ] Documentation was updated if applicable
- [ ] Commit message follows convention

### PR Template

```markdown
## Description

Brief description of the changes.

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change
- [ ] Documentation

## Checklist

- [ ] My code follows the project style
- [ ] I have done self-review of my code
- [ ] I have commented complex code
- [ ] I have updated documentation
- [ ] My changes don't generate warnings
- [ ] I have added tests

## Screenshots (if applicable)

Add screenshots of UI changes.
```

---

## Code Style

### C#

```csharp
// Good
public async Task<Result> ProcessFileAsync(string filePath, CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(filePath);

    var result = await _service.ProcessAsync(filePath, ct);
    return result;
}

// Bad
public async Task<Result> processFile(string file_path, CancellationToken cancellationToken) {
    if (file_path == null) throw new ArgumentNullException();
    var result = await _service.ProcessAsync(file_path, cancellationToken);
    return result;
}
```

**General rules:**

- Use `PascalCase` for types and public methods
- Use `camelCase` for local variables and parameters
- Use `_camelCase` for private fields
- Prefer `var` when the type is obvious
- Use expression bodies for simple methods
- Document public methods with XML comments

### CSS

```css
/* Good - use CSS variables */
.button {
    background: var(--accent-primary);
    border-radius: var(--radius-md);
    transition: var(--transition-base);
}

/* Bad - hardcoded values */
.button {
    background: #6366f1;
    border-radius: 12px;
    transition: all 0.25s ease;
}
```

### JavaScript

```javascript
// Good
const handlePeerConnection = async (peerId) => {
    try {
        await this.sendToBackend('connect', { peerId });
        this.showNotification('Connected!');
    } catch (error) {
        console.error('Connection failed:', error);
    }
};

// Bad
function handlePeerConnection(peerId) {
    this.sendToBackend('connect', { peerId: peerId }).then(function() {
        this.showNotification('Connected!');
    });
}
```

---

## Commits

### Format

```
<type>: <description>

[optional body]

[optional footer]
```

### Types

| Type | Description |
|------|-------------|
| `Add` | New feature |
| `Fix` | Bug fix |
| `Update` | Update to existing feature |
| `Remove` | Code/feature removal |
| `Refactor` | Refactoring without functional change |
| `Docs` | Documentation changes |
| `Style` | Format changes (don't affect logic) |
| `Test` | Add or modify tests |
| `Chore` | Maintenance tasks |

### Examples

```bash
# Good commits
git commit -m "Add: file transfer resume functionality"
git commit -m "Fix: mDNS discovery not finding peers on some networks"
git commit -m "Update: improve handshake timeout handling"
git commit -m "Docs: add API documentation for PeerManager"

# Bad commits
git commit -m "fix stuff"
git commit -m "WIP"
git commit -m "asdfasdf"
```

---

## Labels

| Label | Description |
|-------|-------------|
| `bug` | Something isn't working correctly |
| `enhancement` | New feature or improvement |
| `documentation` | Documentation improvements |
| `good first issue` | Good for new contributors |
| `help wanted` | Extra help needed |
| `question` | Question or discussion |
| `wontfix` | Won't be worked on |

---

## Questions

Have questions? Open a [Discussion](https://github.com/Nicoo01x/SyncBeam/discussions) or an issue with the `question` label.

---

<div align="center">

**Thank you for contributing!**

</div>
