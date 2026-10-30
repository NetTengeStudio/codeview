using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Rec {
    public sealed class CameraOcclusion : IDisposable {
        private BasicEffect effect;
        private readonly VertexPositionColor[] vertices=new VertexPositionColor[6*256];
        public void Draw(GraphicsDevice device, Matrix transform, float x, float y, float halfW, float halfH, IReadOnlyList<SceneObject> scene) {
            int cursor=0; bool prepared=false;
            float reach=2*MathF.Sqrt(halfW*halfW+halfH*halfH);
            for (int i=0; i<scene.Count; i++) {
                SceneShadow shadow=scene[i].Shadow; if (shadow==null) { continue; }
                if (!prepared) {
                    if (effect==null || effect.IsDisposed || !ReferenceEquals(effect.GraphicsDevice,device)) {
                        effect?.Dispose(); effect=new BasicEffect(device) { VertexColorEnabled=true, TextureEnabled=false, LightingEnabled=false };
                    }
                    effect.World=transform; effect.View=Matrix.Identity;
                    effect.Projection=Matrix.CreateOrthographicOffCenter(0,device.Viewport.Width,device.Viewport.Height,0,0,1);
                    device.BlendState=BlendState.Opaque; device.DepthStencilState=DepthStencilState.None; device.RasterizerState=RasterizerState.CullNone;
                    prepared=true;
                }
                foreach (ShadowEdge e in shadow.Edges) {
                    if (!SceneShadow.Project(e,x,y,reach,out ShadowEdge far)) { continue; }
                    var a=new VertexPositionColor(new Vector3(e.AX,-e.AY,0),Color.Black);
                    var b=new VertexPositionColor(new Vector3(e.BX,-e.BY,0),Color.Black);
                    var c=new VertexPositionColor(new Vector3(far.AX,-far.AY,0),Color.Black);
                    var d=new VertexPositionColor(new Vector3(far.BX,-far.BY,0),Color.Black);
                    vertices[cursor++]=a; vertices[cursor++]=b; vertices[cursor++]=c;
                    vertices[cursor++]=c; vertices[cursor++]=b; vertices[cursor++]=d;
                    if (cursor==vertices.Length) { Submit(device,cursor); cursor=0; }
                }
            }
            if (cursor!=0) { Submit(device,cursor); }
        }
        private void Submit(GraphicsDevice device,int count) {
            var passes=effect.CurrentTechnique.Passes;
            for (int i=0; i<passes.Count; i++) { passes[i].Apply(); device.DrawUserPrimitives(PrimitiveType.TriangleList,vertices,0,count/3); }
        }
        public void Dispose() { effect?.Dispose(); effect=null; }
    }
}
