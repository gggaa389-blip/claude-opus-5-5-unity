using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
public partial class CommonMechanicsSuite {
 IEnumerator RecordBuildingVariants(){var parts=new List<string>();for(int mat=0;mat<3;mat++)for(int shape=0;shape<4;shape++){
  string id="building__"+((BuildMat)mat).ToString().ToLowerInvariant()+"_"+((PieceType)shape).ToString().ToLowerInvariant();parts.Add(id);if(Done(id))continue;
  foreach(var d in Destructible.Built.ToArray())if(d)Destroy(d.gameObject);yield return null;yield return null;BuildSystem.Reset();Stage(site);
  if(shape==3){var foot=Feet+Vector3.forward*4;BuildSystem.Place(pc.me,BuildSystem.Compute(PieceType.Wall,foot,0,8),(BuildMat)mat);BuildSystem.Place(pc.me,BuildSystem.Compute(PieceType.Wall,foot,90,8),(BuildMat)mat);BuildSystem.Place(pc.me,BuildSystem.Compute(PieceType.Wall,foot,-90,8),(BuildMat)mat);yield return Wait(7);}
  yield return Wait(.3f);_build=shape;yield return Wait(.4f);for(int n=0;n<3&&(int)pc.buildMat!=mat;n++){_aimTap=true;yield return Wait(.2f);}yield return Wait(.5f);
  yield return Take(id,"Building / "+(BuildMat)mat+" / "+(PieceType)shape,BuildingVariant(mat));
 }MechanicsTopicJoin.Write(root,"building","Building / all twelve material and shape combinations",model,parts.ToArray());}
 IEnumerator BuildingVariant(int mat){step="Placement preview";yield return Wait(3);step="Place / resource cost / complete assembly";_tap=true;yield return Wait(BuildRules.BuildTime((BuildMat)mat)+2);_slot=0;yield return Wait(1);step="Inspect completed piece";_move=new Vector2(.25f,0);yield return Wait(1.2f);_move=Vector2.zero;yield return Wait(3);}
}
