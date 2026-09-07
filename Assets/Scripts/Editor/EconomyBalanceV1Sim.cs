#if UNITY_EDITOR
// DEV-015 确定性经济模拟 v2（Editor-only）。模型更贴近真实现：
//  - 每趟沿竖井下潜 depthD 格（沿途挖矿）→ 每格以带密度生成 vein → 逐【块】装入（HandleTileDug 语义：
//    满则按 valuePerWeight 丢最差 1 块再放新块）→ 保留多样混合货，不出现"全被单一种密度最高矿替换"退化。
//  - 燃料：移动耗油按每格穿越 + 挖掘耗油按 digTime/digMult 计入；返航上行按载重惩罚。
// 输出各 Region × 装备配置的 cargo 价值/重量/v-w/燃料余量/首升趟数。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    public static class EconomyBalanceV1Sim
    {
        const string OutPath = "C:/Users/58058/.workbuddy/tools/d15_economy_sim.txt";
        const string DataFolder = "Assets/Ashfall/Data";
        static TileDefinition Load(string f) => AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/{f}.asset");

        sealed class Band
        {
            public string name; public int minDepth, maxDepth, vMin, vMax;
            public float freq;
            public TileDefinition[] ores; public float[] w;
        }

        sealed class Out
        {
            public string region, cfg;
            public int cargoValue; public float weight, cap, vpw, downFuel, upFuel, margin; public int runs;
        }

        [MenuItem("灰烬之下/DEV-015 经济模拟：Shallow-Mid-Deep × 装备配置")]
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== DEV-015 经济模拟 v2（可信模型：逐块价值密度替换 + 真实燃料）====");
            var iron = Load("Iron_铁矿"); var tin = Load("Tin_锡矿"); var copper = Load("Copper_铜矿");
            var silver = Load("Silver_银矿"); var gold = Load("Gold_金矿"); var emerald = Load("Emerald_绿宝石");
            var platinum = Load("Platinum_铂金"); var diamond = Load("Diamond_钻石");
            var dataF = Load("AncientDataFragment_古代数据碎片"); var alloy = Load("AncientAlloy_古代合金");

            sb.AppendLine("---- 资源表 ----");
            foreach (var o in new[]{iron,tin,copper,silver,gold,emerald,platinum,diamond,dataF,alloy})
                if (o!=null) sb.AppendLine($"  {o.name,-10} val={o.value,-6} w={o.weight,4} v/w={o.value/Mathf.Max(.0001f,o.weight),6:0.0} hard={o.hardness}");

            var bands = new[]
            {
                new Band{name="Shallow",minDepth=DepthRegionLayout.Shallow.minDepth,maxDepth=DepthRegionLayout.Shallow.maxDepth,
                    ores=new[]{iron,tin,copper},w=new float[]{45,40,15},vMin=2,vMax=4,freq=0.5f},
                new Band{name="Mid",minDepth=DepthRegionLayout.Mid.minDepth,maxDepth=DepthRegionLayout.Mid.maxDepth,
                    ores=new[]{copper,silver,gold},w=new float[]{50,35,15},vMin=3,vMax=5,freq=0.6f},
                new Band{name="Deep",minDepth=DepthRegionLayout.Deep.minDepth,maxDepth=DepthRegionLayout.Deep.maxDepth,
                    ores=new[]{gold,platinum,emerald,diamond},w=new float[]{40,30,20,10},vMin=4,vMax=6,freq=0.6f},
            };

            var cfg = new[]{
                new object[]{"Lv0",0,0,0,0,false,false},
                new object[]{"Lv1+电机",1,1,1,0,true,false},
                new object[]{"Lv2+电机货架",2,2,2,1,true,true},
            };

            var outs=new List<Out>();
            var rng=new System.Random(20260915);
            sb.AppendLine("\n---- Region × 配置 模拟 ----");
            foreach(var b in bands){
                int d=b.name=="Shallow"?18:(b.name=="Mid"?40:58);
                foreach(var c in cfg){
                    var o=Sim(b,d,(string)c[0],(int)c[1],(int)c[2],(int)c[3],(int)c[4],(bool)c[5],(bool)c[6],rng);
                    outs.Add(o);
                    sb.AppendLine($"  [{b.name}|{o.cfg}] cargo ${o.cargoValue} (w {o.weight,4:0}/{o.cap,3:0}) v/w {o.vpw,5:0.0} fuel用{Math.Max(o.downFuel,o.upFuel),4:0}/max{(int)EquipmentCatalog.MaxFuel((int)c[2])} 返航余量{o.margin,6:0.0} 首升≈{o.runs}趟");
                }
            }
            try{File.WriteAllText(OutPath,sb.ToString());}catch(Exception e){Debug.LogError("[DEV-015] "+e.Message);}
            Debug.Log("[DEV-015] 模拟完成 → "+OutPath);
        }

        static Out Sim(Band b,int depthD,string label,int dl,int fl,int cl,int ml,bool motor,bool rack,System.Random rng){
            var o=new Out{region=b.name,cfg=label};
            float cap=EquipmentCatalog.MaxCarryWeight(cl)+(rack?EquipmentCatalog.CargoRackCarryBonus:0);
            o.cap=cap;
            float digMult=EquipmentCatalog.DigSpeedMultiplier(dl)*(motor?1f:1f); // 电机不加速挖掘
            float moveMult=motor?EquipmentCatalog.EfficientMotorFuelMult:1f;
            // 货舱：逐块
            var list=new List<(float vpw,float w,int v)>();
            float weight=0; int value=0;
            int guard=0;
            while(weight<cap-0.001f && guard++<depthD*8){
                // 每 ~2 格命中一次 vein（freq）
                if((float)rng.NextDouble()>b.freq) continue;
                int pick=Pick(b,rng); var def=b.ores[pick];
                float vpw=def.value/Mathf.Max(.0001f,def.weight);
                int vein=rng.Next(b.vMin,b.vMax+1);
                for(int k=0;k<vein && weight<cap-0.001f;k++){
                    // 单块入
                    if(weight+def.weight<=cap+0.0001f){weight+=def.weight;value+=def.value;list.Add((vpw,def.weight,def.value));}
                    else{
                        // 满：找最低密度块丢，换更高密度的
                        int worst=-1; float wmin=float.MaxValue;
                        for(int i=0;i<list.Count;i++) if(list[i].vpw<vpw && list[i].vpw<wmin){wmin=list[i].vpw;worst=i;}
                        if(worst<0) break;
                        weight-=list[worst].w; value-=list[worst].v; list.RemoveAt(worst);
                        if(weight+def.weight<=cap+0.0001f){weight+=def.weight;value+=def.value;list.Add((vpw,def.weight,def.value));}
                    }
                }
            }
            // 计算 v/w（总价值/总重）
            o.cargoValue=value; o.weight=weight; o.vpw=weight>0.001f?value/weight:0;
            // 燃料
            float digPerCell=0.35f/digMult;              // drillTime/digMult
            float movePerCell=(1f/4.0f);                  // moveSpeed≈4 → 约 0.25s/格
            float downPerCell=movePerCell*1.1f*moveMult + digPerCell*2.0f;
            o.downFuel=depthD*downPerCell;
            float load=Mathf.Clamp01(weight/cap);
            o.upFuel=depthD*1.1f*moveMult*2.2f*(1f+load*0.35f);
            float maxF=EquipmentCatalog.MaxFuel(fl);
            o.margin=maxF-o.downFuel-o.upFuel;
            o.runs=Mathf.Max(0,(int)Mathf.Ceil(60f/Mathf.Max(1,value)));
            return o;
        }
        static int Pick(Band b,System.Random rng){float t=0;foreach(var w in b.w)t+=w;float r=(float)rng.NextDouble()*t;for(int i=0;i<b.w.Length;i++){r-=b.w[i];if(r<=0)return i;}return b.w.Length-1;}
    }
}
#endif
