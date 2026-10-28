namespace ScheduledTasks;

// One scheduled task as shown in the list.
public class TaskRow
{
    public string Folder { get; set; }      // e.g. "\" or "\Microsoft\Windows\Defrag"
    public string Name { get; set; }
    public string State { get; set; }       // On / Off / Running / Queued
    public string StateKind { get; set; }   // on / off / run
    public bool Mine { get; set; }
    public string Schedule { get; set; }
    public string Command { get; set; }
    public string File { get; set; }
    public string Description { get; set; }
    public DateTime? LastRun { get; set; }
    public DateTime? NextRun { get; set; }
    public string LastText { get; set; }
    public string NextText { get; set; }
    public string Result { get; set; }
    public string ResultKind { get; set; }  // ok / run / never / bad
    public bool Problem { get; set; }
    public string Key => Folder + "\\" + Name;
}
