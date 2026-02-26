namespace DbSync.Core.Models;

public class SyncHistory
{
    public int Id { get; set; }
    public int ClienteId { get; set; }
    public Ambiente AmbienteOrigen { get; set; }
    public Ambiente AmbienteDestino { get; set; }
    public string NombreObjeto { get; set; } = string.Empty;
    public string TipoObjeto { get; set; } = string.Empty;
    public string AccionRealizada { get; set; } = string.Empty;  // CREATE, ALTER, DROP
    public string ScriptEjecutado { get; set; } = string.Empty;
    public string? DefinicionAnterior { get; set; }
    public DateTime FechaEjecucion { get; set; } = DateTime.Now;
    public string? Usuario { get; set; }
    public bool Exitoso { get; set; } = true;
    public string? Error { get; set; }

    public Cliente? Cliente { get; set; }
}
