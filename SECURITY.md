# Política de Seguridad

## Versiones Soportadas

| Versión | Soportada          |
| ------- | ------------------ |
| 1.x.x   | :white_check_mark: |

## Reportar una Vulnerabilidad

La seguridad de SyncBeam es una prioridad. Si descubres una vulnerabilidad de seguridad, por favor repórtala de manera responsable.

### Cómo Reportar

**NO** abras un issue público para vulnerabilidades de seguridad.

En su lugar:

1. **Email**: Envía un email detallado a security@syncbeam.dev (o abre un Security Advisory privado en GitHub)
2. **Incluye**:
   - Descripción de la vulnerabilidad
   - Pasos para reproducir
   - Impacto potencial
   - Sugerencias de mitigación (si las tienes)

### Qué Esperar

- **Confirmación**: Recibirás confirmación dentro de 48 horas
- **Evaluación**: Evaluaremos la vulnerabilidad en 7 días
- **Resolución**: Trabajaremos en un fix y coordinaremos el disclosure
- **Crédito**: Te daremos crédito en el changelog (si lo deseas)

### Alcance

Las siguientes áreas son de especial interés:

- Bypass de autenticación
- Vulnerabilidades en el handshake Noise Protocol
- Fugas de información sensible
- Ejecución remota de código
- Ataques de denegación de servicio

### Fuera de Alcance

- Ataques que requieren acceso físico al dispositivo
- Social engineering
- Ataques contra infraestructura de terceros

## Mejores Prácticas de Seguridad

### Para Usuarios

1. **Mantén el secreto privado**: No compartas tu `~/.secret` públicamente
2. **Red de confianza**: Solo usa SyncBeam en redes de confianza
3. **Actualiza**: Mantén SyncBeam actualizado
4. **Verifica peers**: Confirma la identidad de los peers antes de conectar

### Para Desarrolladores

1. **No hardcodees secretos**: Usa el sistema de configuración
2. **Valida input**: Siempre valida datos de peers remotos
3. **Usa crypto seguro**: No implementes tu propia criptografía
4. **Revisa dependencias**: Mantén dependencias actualizadas

## Arquitectura de Seguridad

```
┌──────────────────────────────────────────────────────────┐
│                    CAPA DE APLICACIÓN                     │
│  - Validación de archivos                                │
│  - Sanitización de clipboard                             │
├──────────────────────────────────────────────────────────┤
│                    CAPA DE TRANSPORTE                     │
│  - AES-256-GCM para cifrado                             │
│  - Nonces incrementales                                  │
│  - Verificación de integridad                            │
├──────────────────────────────────────────────────────────┤
│                    CAPA DE HANDSHAKE                      │
│  - Noise Protocol XX                                     │
│  - Forward secrecy con claves efímeras                  │
│  - Autenticación mutua con Ed25519                       │
├──────────────────────────────────────────────────────────┤
│                    CAPA DE IDENTIDAD                      │
│  - Ed25519 para firmas                                   │
│  - X25519 para key agreement                             │
│  - SHA-256 para hashing                                  │
└──────────────────────────────────────────────────────────┘
```

## Auditorías

Este proyecto no ha sido auditado formalmente. El uso es bajo tu propio riesgo.

Si deseas patrocinar una auditoría de seguridad, por favor contáctanos.
