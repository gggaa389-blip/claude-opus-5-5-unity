using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
public static class MechanicsBuild {
 public static void Build(){
  var output=Environment.GetEnvironmentVariable("MECHANICS_OUTPUT");
  if(string.IsNullOrEmpty(output))throw new Exception("MECHANICS_OUTPUT is required");
  Directory.CreateDirectory(output);
  var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{"Assets/Scenes/Island.unity"},locationPathName=Path.Combine(output,"MechanicsCapture.exe"),target=BuildTarget.StandaloneWindows64,options=BuildOptions.None});
  if(result.summary.result!=BuildResult.Succeeded)throw new Exception("Mechanics capture build failed: "+result.summary.result);
 }
}
