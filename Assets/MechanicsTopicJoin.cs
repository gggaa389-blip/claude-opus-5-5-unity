using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public static class MechanicsTopicJoin {
 [Serializable]class Part {public int frames,hits,pieces,shots,harvest;public float seconds;public string[] states;}
 [Serializable]class Join {public string source="Complete native Unity takes; edits occur only between completed demonstrations with opening and closing handles",status="captured",output;public string[] inputs;}
 [Serializable]class Evidence {public string id,title,model,file,status="captured-unreviewed",setup="Isolated scenes, no active enemy AI",notes="A single topic assembled from complete native takes. Fixtures are reset only while capture is stopped. Each action has opening and closing handles.";public int frames,hits,pieces,shots,harvest;public float seconds;public string[] states,takes;}
 public static void Write(string directory,string id,string title,string model,string[] parts){
  foreach(var p in parts)if(!File.Exists(Path.Combine(directory,p+".evidence.json"))||!File.Exists(Path.Combine(directory,p+".capture.json")))throw new Exception("Missing complete topic take: "+p);
  var ev=parts.Select(p=>JsonUtility.FromJson<Part>(File.ReadAllText(Path.Combine(directory,p+".evidence.json")))).ToArray();
  File.WriteAllText(Path.Combine(directory,id+".join.json"),JsonUtility.ToJson(new Join{output=Path.Combine(directory,id+".mp4"),inputs=parts.Select(p=>Path.Combine(directory,p+".mp4")).ToArray()},true));
  File.WriteAllText(Path.Combine(directory,id+".evidence.json"),JsonUtility.ToJson(new Evidence{id=id,title=title,model=model,file=id+".mp4",takes=parts,frames=ev.Sum(p=>p.frames),seconds=ev.Sum(p=>p.seconds),hits=ev.Sum(p=>p.hits),pieces=ev.Sum(p=>p.pieces),shots=ev.Sum(p=>p.shots),harvest=ev.Sum(p=>p.harvest),states=ev.SelectMany(p=>p.states??new string[0]).Distinct().ToArray()},true));
  File.Copy(Path.Combine(directory,parts[0]+".preview.jpg"),Path.Combine(directory,id+".preview.jpg"),true);
 }
}
public static class MechanicsSafe {
 public static IEnumerator Run(IEnumerator action,Action<Exception> failed){var stack=new Stack<IEnumerator>();stack.Push(action);int frames=0;while(stack.Count>0){bool moved=false;object current=null;Exception error=null;try{moved=stack.Peek().MoveNext();if(moved)current=stack.Peek().Current;}catch(Exception e){error=e;}if(error!=null){failed(error);yield break;}if(!moved){stack.Pop();continue;}if(current is IEnumerator child){stack.Push(child);continue;}if(++frames>12000){failed(new Exception("Demonstration exceeded its bounded duration"));yield break;}yield return current;}}
}
