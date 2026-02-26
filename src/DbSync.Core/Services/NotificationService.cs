using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DbSync.Core.Data;
using DbSync.Core.Models;

namespace DbSync.Core.Services;

/// <summary>
/// Servicio de notificaciones por email.
/// Envía alertas de cambios detectados y errores de conexión después de cada scan.
/// </summary>
public class NotificationService
{
    private readonly CentralRepository _centralRepo;
    private readonly AppDbContext _db;
    private readonly SmtpSettings _smtp;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        CentralRepository centralRepo,
        AppDbContext db,
        IOptions<SmtpSettings> smtpSettings,
        ILogger<NotificationService> logger)
    {
        _centralRepo = centralRepo;
        _db = db;
        _smtp = smtpSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Procesa notificaciones pendientes después de un scan.
    /// Envía email con cambios detectados y errores si los hay.
    /// </summary>
    public async Task ProcessAfterScanAsync(ScanLog scanLog, List<ScanLogEntry> entries, CancellationToken ct = default)
    {
        if (!_smtp.Enabled || _smtp.Recipients.Count == 0)
        {
            _logger.LogDebug("Notificaciones SMTP deshabilitadas o sin destinatarios");
            return;
        }

        try
        {
            // Obtener cambios no notificados
            var pendingChanges = await _centralRepo.GetPendingNotificationsAsync(ct);

            var failedEntries = entries.Where(e => !e.Success).ToList();
            var hasChanges = pendingChanges.Count > 0;
            var hasErrors = failedEntries.Count > 0;

            if (!hasChanges && !hasErrors)
            {
                _logger.LogDebug("Sin cambios ni errores para notificar en scan {ScanId}", scanLog.Id);
                return;
            }

            // Construir y enviar email
            var subject = BuildSubject(scanLog, pendingChanges.Count, failedEntries.Count);
            var body = BuildHtmlBody(scanLog, pendingChanges, failedEntries);

            await SendEmailAsync(subject, body, ct);

            // Marcar cambios como notificados
            if (pendingChanges.Count > 0)
            {
                var changeIds = pendingChanges.Select(c => c.Id).ToList();
                await _centralRepo.MarkNotificationSentAsync(changeIds, ct);
            }

            _logger.LogInformation(
                "Notificación enviada para scan {ScanId}: {Changes} cambios, {Errors} errores",
                scanLog.Id, pendingChanges.Count, failedEntries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando notificación para scan {ScanId}", scanLog.Id);
        }
    }

    /// <summary>
    /// Envía un email de prueba para verificar la configuración SMTP.
    /// </summary>
    public async Task<(bool Success, string Message)> SendTestEmailAsync(CancellationToken ct = default)
    {
        if (!_smtp.Enabled)
            return (false, "SMTP no está habilitado en la configuración");

        if (_smtp.Recipients.Count == 0)
            return (false, "No hay destinatarios configurados");

        try
        {
            var subject = "MKY-sync - Email de Prueba";
            var body = @"
<html><body style='font-family: Arial, sans-serif;'>
<h2 style='color: #333;'>MKY-sync - Notificaciones</h2>
<p>Este es un email de prueba. Si lo recibis, la configuración SMTP está correcta.</p>
<p style='color: #666; font-size: 12px;'>Enviado: " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") + @"</p>
</body></html>";

            await SendEmailAsync(subject, body, ct);
            return (true, $"Email de prueba enviado a {string.Join(", ", _smtp.Recipients)}");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    private string BuildSubject(ScanLog scanLog, int changeCount, int errorCount)
    {
        var parts = new List<string>();

        if (changeCount > 0)
            parts.Add($"{changeCount} cambio{(changeCount > 1 ? "s" : "")}");

        if (errorCount > 0)
            parts.Add($"{errorCount} error{(errorCount > 1 ? "es" : "")}");

        var statusEmoji = scanLog.Status switch
        {
            ScanStatus.Completed => "[OK]",
            ScanStatus.CompletedWithErrors => "[WARN]",
            _ => "[ERROR]"
        };

        return $"{statusEmoji} MKY-sync Scan #{scanLog.Id} - {string.Join(", ", parts)}";
    }

    private string BuildHtmlBody(ScanLog scanLog, List<DetectedChange> changes, List<ScanLogEntry> failedEntries)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"<html><body style='font-family: Arial, sans-serif; color: #333;'>");

        // Header
        sb.AppendLine($@"<h2 style='border-bottom: 2px solid #007bff; padding-bottom: 8px;'>
            MKY-sync - Scan #{scanLog.Id}</h2>");

        // Resumen del scan
        var duration = scanLog.CompletedAt.HasValue
            ? (scanLog.CompletedAt.Value - scanLog.StartedAt).TotalSeconds.ToString("F0") + "s"
            : "en progreso";

        sb.AppendLine(@"<table style='border-collapse: collapse; margin-bottom: 20px;'>");
        sb.AppendLine($@"<tr><td style='padding: 4px 12px 4px 0; font-weight: bold;'>Estado:</td>
            <td style='padding: 4px 0;'>{scanLog.Status}</td></tr>");
        sb.AppendLine($@"<tr><td style='padding: 4px 12px 4px 0; font-weight: bold;'>Inicio:</td>
            <td style='padding: 4px 0;'>{scanLog.StartedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss}</td></tr>");
        sb.AppendLine($@"<tr><td style='padding: 4px 12px 4px 0; font-weight: bold;'>Duracion:</td>
            <td style='padding: 4px 0;'>{duration}</td></tr>");
        sb.AppendLine($@"<tr><td style='padding: 4px 12px 4px 0; font-weight: bold;'>Clientes:</td>
            <td style='padding: 4px 0;'>{scanLog.TotalClientes}</td></tr>");
        sb.AppendLine($@"<tr><td style='padding: 4px 12px 4px 0; font-weight: bold;'>Objetos:</td>
            <td style='padding: 4px 0;'>{scanLog.TotalObjectsScanned}</td></tr>");
        sb.AppendLine(@"</table>");

        // Cambios detectados
        if (changes.Count > 0)
        {
            sb.AppendLine(@"<h3 style='color: #007bff;'>Cambios Detectados</h3>");
            sb.AppendLine(@"<table style='border-collapse: collapse; width: 100%; margin-bottom: 20px;'>");
            sb.AppendLine(@"<tr style='background-color: #f8f9fa;'>
                <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Cliente</th>
                <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Ambiente</th>
                <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Objeto</th>
                <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Tipo</th>
                <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Cambio</th>
            </tr>");

            foreach (var change in changes)
            {
                var changeColor = change.ChangeType switch
                {
                    ObjectChangeType.Created => "#28a745",
                    ObjectChangeType.Modified => "#ffc107",
                    ObjectChangeType.Deleted => "#dc3545",
                    _ => "#6c757d"
                };

                var changeLabel = change.ChangeType switch
                {
                    ObjectChangeType.Created => "Nuevo",
                    ObjectChangeType.Modified => "Modificado",
                    ObjectChangeType.Deleted => "Eliminado",
                    _ => change.ChangeType.ToString()
                };

                sb.AppendLine($@"<tr>
                    <td style='padding: 6px 8px; border: 1px solid #dee2e6;'>{change.ClienteCodigo}</td>
                    <td style='padding: 6px 8px; border: 1px solid #dee2e6;'>{change.Ambiente}</td>
                    <td style='padding: 6px 8px; border: 1px solid #dee2e6; font-family: monospace;'>{change.ObjectFullName}</td>
                    <td style='padding: 6px 8px; border: 1px solid #dee2e6;'>{change.ObjectType}</td>
                    <td style='padding: 6px 8px; border: 1px solid #dee2e6;'>
                        <span style='background-color: {changeColor}; color: white; padding: 2px 8px; border-radius: 3px; font-size: 12px;'>
                            {changeLabel}
                        </span>
                    </td>
                </tr>");
            }

            sb.AppendLine(@"</table>");

            // Resumen por cliente
            var byClient = changes.GroupBy(c => c.ClienteCodigo);
            sb.AppendLine(@"<p style='color: #666; font-size: 13px;'><strong>Resumen:</strong> ");
            sb.AppendLine(string.Join(", ", byClient.Select(g =>
                $"{g.Key}: {g.Count(c => c.ChangeType == ObjectChangeType.Created)} nuevos, " +
                $"{g.Count(c => c.ChangeType == ObjectChangeType.Modified)} modificados, " +
                $"{g.Count(c => c.ChangeType == ObjectChangeType.Deleted)} eliminados")));
            sb.AppendLine(@"</p>");
        }

        // Errores de conexión
        if (failedEntries.Count > 0)
        {
            sb.AppendLine(@"<h3 style='color: #dc3545;'>Errores de Conexion</h3>");
            sb.AppendLine(@"<table style='border-collapse: collapse; width: 100%; margin-bottom: 20px;'>");
            sb.AppendLine(@"<tr style='background-color: #f8f9fa;'>
                <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Cliente</th>
                <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Ambiente</th>
                <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Error</th>
            </tr>");

            foreach (var entry in failedEntries)
            {
                sb.AppendLine($@"<tr style='background-color: #fff5f5;'>
                    <td style='padding: 6px 8px; border: 1px solid #dee2e6;'>{entry.ClienteCodigo}</td>
                    <td style='padding: 6px 8px; border: 1px solid #dee2e6;'>{entry.Ambiente}</td>
                    <td style='padding: 6px 8px; border: 1px solid #dee2e6; color: #dc3545;'>{entry.ErrorMessage}</td>
                </tr>");
            }

            sb.AppendLine(@"</table>");
        }

        // Footer
        sb.AppendLine(@"<hr style='border: 0; border-top: 1px solid #dee2e6; margin: 20px 0;'/>");
        sb.AppendLine(@"<p style='color: #999; font-size: 11px;'>
            Este email fue generado automaticamente por MKY-sync.
            Para dejar de recibir estas notificaciones, desactive SMTP en la configuracion.</p>");
        sb.AppendLine(@"</body></html>");

        return sb.ToString();
    }

    private async Task SendEmailAsync(string subject, string htmlBody, CancellationToken ct)
    {
        using var client = new SmtpClient(_smtp.Host, _smtp.Port)
        {
            EnableSsl = _smtp.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrEmpty(_smtp.Username))
        {
            client.Credentials = new NetworkCredential(_smtp.Username, _smtp.Password);
        }

        var from = new MailAddress(_smtp.FromAddress, _smtp.FromName);
        using var message = new MailMessage
        {
            From = from,
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };

        foreach (var recipient in _smtp.Recipients)
        {
            message.To.Add(recipient);
        }

        await client.SendMailAsync(message, ct);
    }

    /// <summary>
    /// Envía notificación de cambios detectados por el scanner a los usuarios asignados al cliente.
    /// Solo envía si el ambiente tiene NotificarCambios habilitado.
    /// </summary>
    public async Task SendChangeNotificationToClientUsersAsync(
        int clienteId,
        string clienteCodigo,
        Ambiente ambiente,
        List<DetectedChange> changes,
        CancellationToken ct = default)
    {
        if (!_smtp.Enabled || changes.Count == 0) return;

        try
        {
            var emails = await GetClientUserEmailsAsync(clienteId, ct);
            if (emails.Count == 0)
            {
                _logger.LogDebug("Sin usuarios con email para notificar cambios en {Cliente}/{Ambiente}",
                    clienteCodigo, ambiente);
                return;
            }

            var subject = $"[MKY-sync] {changes.Count} cambio{(changes.Count > 1 ? "s" : "")} detectado{(changes.Count > 1 ? "s" : "")} en {clienteCodigo}/{ambiente}";
            var body = BuildChangeNotificationHtml(clienteCodigo, ambiente.ToString(), changes);

            await SendEmailToRecipientsAsync(subject, body, emails, ct);

            _logger.LogInformation(
                "Notificación de cambios enviada a {Count} usuarios para {Cliente}/{Ambiente}: {Changes} cambios",
                emails.Count, clienteCodigo, ambiente, changes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando notificación de cambios a usuarios de {Cliente}/{Ambiente}",
                clienteCodigo, ambiente);
        }
    }

    /// <summary>
    /// Envía notificación de sincronización manual a los usuarios asignados al cliente.
    /// </summary>
    public async Task SendSyncNotificationToClientUsersAsync(
        int clienteId,
        string clienteCodigo,
        Ambiente ambienteOrigen,
        Ambiente ambienteDestino,
        List<SyncResult> results,
        string? executedBy,
        CancellationToken ct = default)
    {
        if (!_smtp.Enabled) return;

        var successful = results.Where(r => r.Success).ToList();
        if (successful.Count == 0) return;

        try
        {
            var emails = await GetClientUserEmailsAsync(clienteId, ct);
            if (emails.Count == 0)
            {
                _logger.LogDebug("Sin usuarios con email para notificar sync en {Cliente}", clienteCodigo);
                return;
            }

            var subject = $"[MKY-sync] Sincronización ejecutada en {clienteCodigo}: {ambienteOrigen} → {ambienteDestino} ({successful.Count} objeto{(successful.Count > 1 ? "s" : "")})";
            var body = BuildSyncNotificationHtml(clienteCodigo, ambienteOrigen.ToString(), ambienteDestino.ToString(), results, executedBy);

            await SendEmailToRecipientsAsync(subject, body, emails, ct);

            _logger.LogInformation(
                "Notificación de sync enviada a {Count} usuarios para {Cliente}: {Origen}→{Destino}",
                emails.Count, clienteCodigo, ambienteOrigen, ambienteDestino);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando notificación de sync a usuarios de {Cliente}", clienteCodigo);
        }
    }

    /// <summary>
    /// Obtiene los emails de los usuarios activos asignados a un cliente.
    /// </summary>
    private async Task<List<string>> GetClientUserEmailsAsync(int clienteId, CancellationToken ct)
    {
        return await _db.UsuarioClientes
            .Where(uc => uc.ClienteId == clienteId)
            .Select(uc => uc.Usuario!)
            .Where(u => u.Activo && u.EmailConfirmed && u.Email != null && u.Email != "")
            .Select(u => u.Email!)
            .Distinct()
            .ToListAsync(ct);
    }

    private async Task SendEmailToRecipientsAsync(string subject, string htmlBody, List<string> recipients, CancellationToken ct)
    {
        using var client = new SmtpClient(_smtp.Host, _smtp.Port)
        {
            EnableSsl = _smtp.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrEmpty(_smtp.Username))
        {
            client.Credentials = new NetworkCredential(_smtp.Username, _smtp.Password);
        }

        var from = new MailAddress(_smtp.FromAddress, _smtp.FromName);
        using var message = new MailMessage
        {
            From = from,
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };

        foreach (var email in recipients)
        {
            message.To.Add(email);
        }

        await client.SendMailAsync(message, ct);
    }

    private string BuildChangeNotificationHtml(string clienteCodigo, string ambiente, List<DetectedChange> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<html><body style='font-family: Arial, sans-serif; color: #333;'>");
        sb.AppendLine($"<h2 style='border-bottom: 2px solid #007bff; padding-bottom: 8px;'>Cambios detectados en {clienteCodigo} / {ambiente}</h2>");
        sb.AppendLine($"<p>Se detectaron <strong>{changes.Count}</strong> cambio{(changes.Count > 1 ? "s" : "")} en el ambiente <strong>{ambiente}</strong>:</p>");

        sb.AppendLine("<table style='border-collapse: collapse; width: 100%; margin-bottom: 20px;'>");
        sb.AppendLine(@"<tr style='background-color: #f8f9fa;'>
            <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Objeto</th>
            <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Tipo</th>
            <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Cambio</th>
        </tr>");

        foreach (var change in changes)
        {
            var (color, label) = change.ChangeType switch
            {
                ObjectChangeType.Created => ("#28a745", "Nuevo"),
                ObjectChangeType.Modified => ("#ffc107", "Modificado"),
                ObjectChangeType.Deleted => ("#dc3545", "Eliminado"),
                _ => ("#6c757d", change.ChangeType.ToString())
            };

            sb.AppendLine($@"<tr>
                <td style='padding: 6px 8px; border: 1px solid #dee2e6; font-family: monospace;'>{change.ObjectFullName}</td>
                <td style='padding: 6px 8px; border: 1px solid #dee2e6;'>{change.ObjectType}</td>
                <td style='padding: 6px 8px; border: 1px solid #dee2e6;'>
                    <span style='background-color: {color}; color: white; padding: 2px 8px; border-radius: 3px; font-size: 12px;'>{label}</span>
                </td>
            </tr>");
        }

        sb.AppendLine("</table>");
        sb.AppendLine($"<p style='color: #666; font-size: 13px;'>Detectado: {DateTime.Now:dd/MM/yyyy HH:mm}</p>");
        sb.AppendLine("<hr style='border: 0; border-top: 1px solid #dee2e6; margin: 20px 0;'/>");
        sb.AppendLine("<p style='color: #999; font-size: 11px;'>Este email fue generado automaticamente por MKY-sync. Contacte al administrador para dejar de recibir estas notificaciones.</p>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }

    private string BuildSyncNotificationHtml(string clienteCodigo, string origen, string destino, List<SyncResult> results, string? executedBy)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<html><body style='font-family: Arial, sans-serif; color: #333;'>");
        sb.AppendLine($"<h2 style='border-bottom: 2px solid #28a745; padding-bottom: 8px;'>Sincronización ejecutada en {clienteCodigo}</h2>");
        sb.AppendLine($"<p><strong>{origen} → {destino}</strong></p>");

        if (!string.IsNullOrEmpty(executedBy))
            sb.AppendLine($"<p>Ejecutado por: <strong>{executedBy}</strong></p>");

        var successful = results.Where(r => r.Success).ToList();
        var failed = results.Where(r => !r.Success).ToList();

        sb.AppendLine($"<p>{successful.Count} objeto{(successful.Count > 1 ? "s" : "")} sincronizado{(successful.Count > 1 ? "s" : "")}");
        if (failed.Count > 0)
            sb.Append($", <span style='color: #dc3545;'>{failed.Count} con error</span>");
        sb.AppendLine("</p>");

        sb.AppendLine("<table style='border-collapse: collapse; width: 100%; margin-bottom: 20px;'>");
        sb.AppendLine(@"<tr style='background-color: #f8f9fa;'>
            <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Objeto</th>
            <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Accion</th>
            <th style='padding: 8px; border: 1px solid #dee2e6; text-align: left;'>Resultado</th>
        </tr>");

        foreach (var r in results)
        {
            var rowBg = r.Success ? "" : "background-color: #fff5f5;";
            var statusColor = r.Success ? "#28a745" : "#dc3545";
            var statusLabel = r.Success ? "OK" : "Error";

            sb.AppendLine($@"<tr style='{rowBg}'>
                <td style='padding: 6px 8px; border: 1px solid #dee2e6; font-family: monospace;'>{r.ObjectFullName}</td>
                <td style='padding: 6px 8px; border: 1px solid #dee2e6;'>{r.Action}</td>
                <td style='padding: 6px 8px; border: 1px solid #dee2e6;'>
                    <span style='background-color: {statusColor}; color: white; padding: 2px 8px; border-radius: 3px; font-size: 12px;'>{statusLabel}</span>
                    {(r.Success ? "" : $" <small style='color: #dc3545;'>{r.Message}</small>")}
                </td>
            </tr>");
        }

        sb.AppendLine("</table>");
        sb.AppendLine($"<p style='color: #666; font-size: 13px;'>Ejecutado: {DateTime.Now:dd/MM/yyyy HH:mm}</p>");
        sb.AppendLine("<hr style='border: 0; border-top: 1px solid #dee2e6; margin: 20px 0;'/>");
        sb.AppendLine("<p style='color: #999; font-size: 11px;'>Este email fue generado automaticamente por MKY-sync. Contacte al administrador para dejar de recibir estas notificaciones.</p>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }
}
