using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
public partial class CommonMechanicsSuite {
 void ResetResultRound(){var m=Match.I;m.phase=Match.Phase.Playing;m.winner=null;m.endTime=-1;m.combatants.RemoveAll(a=>a!=pc.me);typeof(Hud).GetField("SummaryAt",BindingFlags.Public|BindingFlags.Static)?.SetValue(null,-1f);if(ReplayPlayer.Active)ReplayPlayer.I.Stop();Stage(site);pc.enabled=true;pc.cam.enabled=true;}
 IEnumerator Results(){
  ResetResultRound();Equip("assault_rifle");SpawnTarget(site+Vector3.forward*8);target.health=60;target.shield=0;Match.I.Register(target);yield return Wait(1);yield return Take("results__victory","Prepared final elimination / victory and summary",VictoryTake(),"A two-participant outcome fixture, with a passive target. Not a real match result.");
  if(target){Match.I.combatants.Remove(target);Destroy(target.gameObject);target=null;}ResetResultRound();SpawnTarget(site+Vector3.forward*8);Match.I.Register(target);var survivor=target;target=null;SpawnTarget(site+new Vector3(8,0,10));Match.I.Register(target);yield return Wait(2);yield return Take("results__defeat","Elimination / death replay and spectator view",DefeatTake(survivor),"Two stationary survivors. A controlled damage event demonstrates death, replay, spectator and summary UI; not a competitive match.");
  MechanicsTopicJoin.Write(root,"results","Victory, elimination, spectator view and summaries",model,new[]{"results__victory","results__defeat"});
 }
 IEnumerator VictoryTake(){_look=target.Chest;step="Final passive opponent";yield return Wait(4);_aim=true;for(int i=0;i<12&&target.Alive;i++){_tap=true;yield return Wait(.5f);}_aim=false;step="Victory presentation";yield return Wait(12);typeof(Hud).GetField("SummaryAt",BindingFlags.Public|BindingFlags.Static)?.SetValue(null,UiClock.Now);step="Match summary";yield return Wait(12);}
 IEnumerator DefeatTake(Combatant killer){_look=killer.Chest;step="Live view before controlled elimination";yield return Wait(6);pc.me.TakeDamage(new DamageEvent{amount=1000,source=killer,point=pc.me.Chest,weapon=WeaponTable.Get("assault_rifle")});step="Death and replay";yield return Wait(13);if(ReplayPlayer.Active)ReplayPlayer.I.Stop();typeof(Hud).GetField("SummaryAt",BindingFlags.Public|BindingFlags.Static)?.SetValue(null,-1f);step="Spectator view";yield return Wait(10);typeof(Hud).GetField("SummaryAt",BindingFlags.Public|BindingFlags.Static)?.SetValue(null,UiClock.Now);step="Result summary";yield return Wait(10);}
}
