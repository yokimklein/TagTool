﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TagTool.IO;
using TagTool.Cache;
using TagTool.Commands.Common;
using TagTool.Common;
using TagTool.Geometry.BspCollisionGeometry;
using TagTool.Tags.Definitions;
using TagTool.Tags;

namespace TagTool.Commands.CollisionModels
{
    public class GenerateCollisionBSPCommand : Command
    {
        private CollisionModel Definition { get; }
        private CollisionGeometry Bsp { get; set; }
        private bool debug = false;
        private int original_surface_count = 0;
        private List<int> surface_addendums = new List<int>();

    public GenerateCollisionBSPCommand(ref CollisionModel definition) :
            base(true,

                "GenerateCollisionBSP",
                "Generates a Collision BSP for the current collision tag, removing the previous collision BSP",

                "GenerateCollisionBSP",

                "")
        {
            Definition = definition;
            Bsp = new CollisionGeometry();
        }

        public override object Execute(List<string> args)
        {
            for (int region_index = 0; region_index < Definition.Regions.Count; region_index++)
            {
                for(int permutation_index = 0; permutation_index < Definition.Regions[region_index].Permutations.Count; permutation_index++)
                {
                    for(int bsp_index = 0; bsp_index < Definition.Regions[region_index].Permutations[permutation_index].Bsps.Count; bsp_index++)
                    {
                        if (!generate_bsp(region_index, permutation_index, bsp_index))
                            return false;
                    }
                }
            }        
            return true;
        }

        public bool generate_bsp(int region_index, int permutation_index, int bsp_index)
        {
            Bsp = Definition.Regions[region_index].Permutations[permutation_index].Bsps[bsp_index].Geometry.DeepClone();

            //make sure there is nothing in the bsp blocks before starting
            Bsp.Leaves.Clear();
            Bsp.Bsp2dNodes.Clear();
            Bsp.Bsp2dReferences.Clear();
            Bsp.Bsp3dNodes.Clear();

            if (!verify_collision_geometry())
            {
                Console.WriteLine($"###Failed to verify collision geometry!");
                return false;
            }

            //populate surface_addendums list for usage later on
            original_surface_count = Bsp.Surfaces.Count;
            for(int surface_index = 0; surface_index < Bsp.Surfaces.Count; surface_index++)
            {
                surface_addendums.Add(surface_index);
            }

            //allocate surface array before starting the bsp build
            surface_array_definition surface_array = new surface_array_definition { free_count = Bsp.Surfaces.Count, used_count = 0, surface_array = new List<int>() };
            for (int i = 0; i < Bsp.Surfaces.Count; i++)
            {
                if (Bsp.Surfaces[i].Flags.HasFlag(SurfaceFlags.Climbable) || Bsp.Surfaces[i].Flags.HasFlag(SurfaceFlags.Breakable))
                    surface_array.surface_array.Add(i);
                else
                    surface_array.surface_array.Add((int)(i | 0x80000000));
            }

            int bsp3dnode_index = -1;
            if (build_bsp_tree_main(surface_array, ref bsp3dnode_index))
            {
                Console.WriteLine($"###Collision bsp R{region_index}P{permutation_index}B{bsp_index} built successfully!");
                Definition.Regions[region_index].Permutations[permutation_index].Bsps[bsp_index].Geometry = Bsp;
            }
            else
            {
                Console.WriteLine($"###Failed to build collision bsp R{region_index}P{permutation_index}B{bsp_index}!");
                return false;
            }
            return true;
        }

        public int bsp_tree_decision_switch(ref surface_array_definition surface_array)
        {
            int surface_array_total_count = surface_array.used_count + surface_array.free_count;
            if (surface_array_total_count <= 0)
                return 2;
            int free_surfaces_positive = 0;
            int used_surfaces_positive = 0;
            int used_surfaces_negative = 0;
            for (int surface_index_index = 0; surface_index_index < surface_array_total_count; surface_index_index++)
            {
                int surface_index = surface_array.surface_array[surface_index_index];
                if(surface_index_index >= surface_array.free_count) //surface is used
                {
                    if (surface_index >= 0)
                        used_surfaces_positive++;
                    else
                        used_surfaces_negative++;
                }
                else //surface is free
                {
                    if (surface_index < 0)
                        return 1; //if there is a free surface with a negative surface index, create bsp3dnodes
                    free_surfaces_positive++;
                }
            }
            bool no_free_surfaces_positive = free_surfaces_positive == 0;
            bool no_used_surfaces_positive = used_surfaces_positive == 0;
            if (free_surfaces_positive > 0)
            {
                if (no_used_surfaces_positive)
                    return 1;
            }
            if (no_free_surfaces_positive && no_used_surfaces_positive)
                return 2;
            if(used_surfaces_negative > 0 || used_surfaces_positive <= 0)
                return surface_array_recalculate_counts(ref surface_array);
            return 3;
        }

        public int surface_array_recalculate_counts(ref surface_array_definition surface_array)
        {
            bool used_count_zero = surface_array.used_count == 0;
            bool used_count_negative = surface_array.used_count < 0;

            surface_array_definition surface_counts = new surface_array_definition();
            surface_array_definition new_surface_array = new surface_array_definition { surface_array = new List<int>(), free_count = 0, used_count = 0 };
            double surface_plane_fits_negative = 0;
            double surface_plane_fits_positive = 0;

            if (!used_count_negative && !used_count_zero)
            {
                for(int surface_index_index = 0; surface_index_index < surface_array.used_count; surface_index_index++)
                {
                    int surface_index = surface_array.surface_array[surface_index_index + surface_array.free_count];
                    double current_surface_plane_fit = check_surface_plane_fit(surface_index & 0x7FFFFFFF);
                    new_surface_array.surface_array.Add(surface_index);
                    if (surface_index < 0)
                    {
                        surface_counts.used_count++;
                        surface_plane_fits_negative += current_surface_plane_fit;
                    }
                    else
                    {
                        surface_counts.free_count++;
                        surface_plane_fits_positive += current_surface_plane_fit;
                    }
                }
            }
            int new_surfaces_count = surface_counts.free_count + surface_counts.used_count;
            new_surface_array.used_count = new_surfaces_count;

            if (surface_plane_fits_negative * 4.0f <= surface_plane_fits_positive)
            {
                surface_array = new_surface_array.DeepClone();
                return 3;
            }

            surface_array_definition new_surface_array_B = new surface_array_definition { surface_array = new List<int>(), free_count = 0, used_count = 0 };
            for (int surface_index_index = 0; surface_index_index < new_surface_array.used_count; surface_index_index++)
            {
                int surface_index = new_surface_array.surface_array[surface_index_index];
                if(surface_index < 0)
                {
                    new_surface_array_B.surface_array.Add(surface_index);
                }
            }
            new_surface_array_B.used_count = new_surface_array_B.surface_array.Count;
            surface_array = new_surface_array_B.DeepClone();
            return 2;
        }

        public double check_surface_plane_fit(int surface_index)
        {
            double surface_plane_fit = 0;
            Surface surface_block = Bsp.Surfaces[surface_index];
            Edge first_edge_block = Bsp.Edges[surface_block.FirstEdge];
            RealPlane3d plane_parameters = plane_get_equation_parameters(surface_block.Plane);

            RealPoint3d first_edge_vertex;
            if (first_edge_block.RightSurface == surface_index)
                first_edge_vertex = Bsp.Vertices[first_edge_block.EndVertex].Point;
            else
                first_edge_vertex = Bsp.Vertices[first_edge_block.StartVertex].Point;

            Edge edge_block = new Edge();
            int edge_index = -1;
            if (first_edge_block.RightSurface == surface_index)
                edge_index = first_edge_block.ReverseEdge;
            else
                edge_index = first_edge_block.ForwardEdge;

            edge_block = Bsp.Edges[edge_index];

            while (true)
            {
                int edge_vertex_A_index = -1;
                int edge_vertex_B_index = -1;
                if (edge_block.RightSurface == surface_index)
                {
                    edge_vertex_A_index = edge_block.EndVertex;
                    edge_vertex_B_index = edge_block.StartVertex;
                }
                else
                {
                    edge_vertex_A_index = edge_block.StartVertex;
                    edge_vertex_B_index = edge_block.EndVertex;
                }

                RealPoint3d edge_vertex_A = Bsp.Vertices[edge_vertex_A_index].Point;
                RealPoint3d edge_vertex_B = Bsp.Vertices[edge_vertex_B_index].Point;

                float XDiff1 = edge_vertex_A.X - first_edge_vertex.X;
                float YDiff1 = edge_vertex_A.Y - first_edge_vertex.Y;
                float ZDiff1 = edge_vertex_A.Z - first_edge_vertex.Z;
                float XDiff2 = edge_vertex_B.X - first_edge_vertex.X;
                float YDiff2 = edge_vertex_B.Y - first_edge_vertex.Y;
                float ZDiff2 = edge_vertex_B.Z - first_edge_vertex.Z;
                float v17 = ZDiff2 * YDiff1 - YDiff2 * ZDiff1;
                float v18 = ZDiff1 * XDiff2 - ZDiff2 * XDiff1;
                float v19 = XDiff1 * YDiff2 - XDiff2 * YDiff1;

                surface_plane_fit = plane_parameters.I * v17 + plane_parameters.J * v18 + plane_parameters.K * v19 + surface_plane_fit;

                if (edge_block.RightSurface == surface_index)
                    edge_index = edge_block.ReverseEdge;
                else
                    edge_index = edge_block.ForwardEdge;
                //break the loop if we have finished circulating the surface
                if (edge_index == surface_block.FirstEdge)
                    break;

                edge_block = Bsp.Edges[edge_index];
            }
            if (surface_plane_fit < 0)
                return 0;

            return surface_plane_fit;
        }

        public RealPlane3d plane_get_equation_parameters(int plane_index)
        {
            RealPlane3d plane_equation = new RealPlane3d();
            Plane plane_block = Bsp.Planes[(short)plane_index & 0x7FFF];
            if((short)plane_index >= 0)
            {
                plane_equation = plane_block.Value;
            }
            else
            {
                plane_equation.I = -plane_block.Value.I;
                plane_equation.J = -plane_block.Value.J;
                plane_equation.K = -plane_block.Value.K;
                plane_equation.D = -plane_block.Value.D;
            }
            return plane_equation;
        }

        public struct coll_bsp3dnode
        {
            public short Plane;
            public byte BackChildLower;
            public byte BackChildMid;
            public byte BackChildUpper;
            public byte FrontChildLower;
            public byte FrontChildMid;
            public byte FrontChildUpper;
        }

        public bool build_bsp_tree_main(surface_array_definition surface_array, ref int bsp3dnode_index)
        {
            switch (bsp_tree_decision_switch(ref surface_array))
            {
                case 1: //construct bsp3d nodes
                    plane_splitting_parameters splitting_parameters = find_surface_splitting_plane(surface_array);
                    //these arrays need to be initialized with empty elements so that specific indices can be directly assigned
                    surface_array_definition back_surface_array = new surface_array_definition {free_count = splitting_parameters.BackSurfaceCount, used_count = splitting_parameters.BackSurfaceUsedCount, surface_array = new List<int>(new int[splitting_parameters.BackSurfaceCount + splitting_parameters.BackSurfaceUsedCount]) };
                    surface_array_definition front_surface_array = new surface_array_definition { free_count = splitting_parameters.FrontSurfaceCount, used_count = splitting_parameters.FrontSurfaceUsedCount, surface_array = new List<int>(new int[splitting_parameters.FrontSurfaceCount + splitting_parameters.FrontSurfaceUsedCount]) };

                    //here we are going to preserve the current counts of the various geometry primitives, so that they can be reset later
                    int vertex_block_count = Bsp.Vertices.Count;
                    int surface_block_count = Bsp.Surfaces.Count;
                    int edges_block_count = Bsp.Edges.Count;

                    if (!split_object_surfaces_with_plane(surface_array, splitting_parameters.plane_index, ref back_surface_array, ref front_surface_array))
                    {
                        Console.WriteLine("###ERROR Failed to split surface!");
                        return false;
                    }
                    Bsp.Bsp3dNodes.Add(new Bsp3dNode { FrontChild = -1, BackChild = -1 });
                    int current_bsp3dnode_index = Bsp.Bsp3dNodes.Count - 1;
                    int back_child_node_index = -1;
                    int front_child_node_index = -1;
                    //this function is recursive, and continues branching until no more 3d nodes can be made
                    if(build_bsp_tree_main(back_surface_array,ref back_child_node_index) && build_bsp_tree_main(front_surface_array, ref front_child_node_index))
                    {
                        UInt64 bsp3dnode_value = 0;
                        coll_bsp3dnode node = new coll_bsp3dnode { FrontChildLower = (byte)0xFF, FrontChildMid = (byte)0xFF, FrontChildUpper = (byte)0xFF, BackChildLower = (byte)0xFF, BackChildMid = (byte)0xFF, BackChildUpper = (byte)0xFF };

                        node.Plane = splitting_parameters.plane_index < 0 ? (short)(splitting_parameters.plane_index | 0x8000) : (short)splitting_parameters.plane_index;

                        if (front_child_node_index != -1)
                        {
                            byte[] frontchildbytes = BitConverter.GetBytes(front_child_node_index);
                            node.FrontChildUpper = frontchildbytes[3];
                            node.FrontChildMid = frontchildbytes[1];
                            node.FrontChildLower = frontchildbytes[0];
                        }

                        if (back_child_node_index != -1)
                        {
                            byte[] backchildbytes = BitConverter.GetBytes(back_child_node_index);
                            node.BackChildUpper = backchildbytes[3];
                            node.BackChildMid = backchildbytes[1];
                            node.BackChildLower = backchildbytes[0];
                        }
                       
                        //data storage format is funky with two 3-byte "ints". This is a homebrewed method to handle it.
                        using (MemoryStream stream = new MemoryStream())
                        {
                            EndianWriter writer = new EndianWriter(stream, EndianFormat.LittleEndian);
                            EndianReader reader = new EndianReader(stream, EndianFormat.LittleEndian);
                            writer.Write(node.Plane);
                            writer.Write(node.BackChildLower);
                            writer.Write(node.BackChildMid);
                            writer.Write(node.BackChildUpper);
                            writer.Write(node.FrontChildLower);
                            writer.Write(node.FrontChildMid);
                            writer.Write(node.FrontChildUpper);
                            stream.Position = 0;
                            bsp3dnode_value = reader.ReadUInt64();
                        }

                        Bsp.Bsp3dNodes[current_bsp3dnode_index] = new Bsp3dNode{ Value = bsp3dnode_value };

                        bsp3dnode_index = current_bsp3dnode_index;
                    }
                    else
                    {
                        Console.WriteLine("###ERROR couldn't build surface lists.");
                        return false;
                    }

                    //here we are going to reset the counts of the various geometry primitives to their prior state
                    while (Bsp.Vertices.Count > vertex_block_count)
                        Bsp.Vertices.RemoveAt(Bsp.Vertices.Count - 1);
                    while (Bsp.Surfaces.Count > surface_block_count)
                        Bsp.Surfaces.RemoveAt(Bsp.Surfaces.Count - 1);
                    while (Bsp.Edges.Count > edges_block_count)
                        Bsp.Edges.RemoveAt(Bsp.Edges.Count - 1);
                    while (surface_addendums.Count > surface_block_count)
                        surface_addendums.RemoveAt(surface_addendums.Count - 1);

                    return true;
                case 2: //build leaves
                    if(surfaces_reset_for_leaf_building(ref surface_array) == -1)
                    {
                        Console.WriteLine("###ERROR tried to build leaves while there are free surfaces.");
                        return false;
                    }
                    int leaf_index = -1;
                    if(build_leaves(ref surface_array, ref leaf_index))
                    {
                        bsp3dnode_index = (int)(leaf_index | 0x80000000);
                        return true;
                    }
                    else
                    {
                        Console.WriteLine("###ERROR couldn't build leaf.");
                        return false;
                    }
                case 3: //all done!
                    bsp3dnode_index = -1;
                    return true;
                default:
                    Console.WriteLine("###ERROR couldn't decide what to build.");
                    return false;
            }
        }

        public int surfaces_reset_for_leaf_building(ref surface_array_definition surface_array)
        {
            if (surface_array.free_count > 0)
                return -1;
            for(int surface_index_index = 0; surface_index_index < surface_array.used_count; surface_index_index++)
            {
                int surface_index = surface_array.surface_array[surface_index_index] & 0x7FFFFFFF;
                while(surface_index >= original_surface_count)
                {
                    surface_index = surface_addendums[surface_index];
                }
                surface_array.surface_array[surface_index_index] = (int)(surface_index | 0x80000000);
            }
            return surface_array.used_count;
        }

        public surface_array_definition collect_plane_matching_surfaces(ref surface_array_definition surface_array, int plane_index)
        {
            surface_array_definition plane_matched_surface_array = new surface_array_definition {surface_array = new List<int>() };
            for (int surface_array_index = 0; surface_array_index < surface_array.used_count; surface_array_index++)
            {
                int surface_index = surface_array.surface_array[surface_array_index];
                int absolute_surface_index = surface_index & 0x7FFFFFFF;
                if(surface_index < 0 && (short)Bsp.Surfaces[absolute_surface_index].Plane == plane_index)
                {
                    plane_matched_surface_array.surface_array.Add(absolute_surface_index);
                    //reset plane matching surfaces in the primary array to an unused state
                    surface_array.surface_array[surface_array_index] = absolute_surface_index;
                }
            }
            return plane_matched_surface_array;
        }

        public int plane_determine_axis_minimum_coefficient(Plane plane_block)
        {
            int minimum_coefficient;
            float plane_I = Math.Abs(plane_block.Value.I);
            float plane_J = Math.Abs(plane_block.Value.J);
            float plane_K = Math.Abs(plane_block.Value.K);
            if (plane_K < plane_J || plane_K < plane_I)
            {
                if (plane_J >= plane_I)
                    minimum_coefficient = 1;
                else
                    minimum_coefficient = 0;
            }
            else
                minimum_coefficient = 2;
            return minimum_coefficient;
        }

        public bool check_plane_projection_parameter_greater_than_0(Plane plane_block, int projection_axis)
        {
            switch (projection_axis)
            {
                case 0: //x axis
                    return plane_block.Value.I > 0.0f;
                case 1: //y axis
                    return plane_block.Value.J > 0.0f;
                case 2: //z axis
                    return plane_block.Value.K > 0.0f;
            }
            return false;
        }

        public void sort_surfaces_by_plane_2D(plane_splitting_parameters splitting_parameters, int plane_projection_axis, int plane_mirror_check, Bsp2dNode bsp2dnode_block, surface_array_definition plane_matched_surface_array, surface_array_definition back_surface_array, surface_array_definition front_surface_array)
        {
            int back_surface_count = 0;
            int front_surface_count = 0;
            foreach(int surface_index in plane_matched_surface_array.surface_array)
            {
                Plane_Relationship surface_location = determine_surface_plane_relationship_2D(surface_index, bsp2dnode_block, plane_projection_axis, plane_mirror_check);
                if (surface_location.HasFlag(Plane_Relationship.BackofPlane))
                {
                    back_surface_array.surface_array[back_surface_count] = surface_index;
                    back_surface_count++;
                }
                if (surface_location.HasFlag(Plane_Relationship.FrontofPlane))
                {
                    front_surface_array.surface_array[front_surface_count] = surface_index;
                    front_surface_count++;
                }
            }
            if (front_surface_count != splitting_parameters.FrontSurfaceCount)
            {
                Console.WriteLine("###ERROR BSP2D front_surface_index_index!=front_surface_index_count");
            }
            if (back_surface_count != splitting_parameters.BackSurfaceCount)
            {
                Console.WriteLine("###ERROR BSP2D back_surface_index_index!=back_surface_index_count");
            }
        }

        public int create_bsp2dnodes(int plane_projection_axis, int plane_mirror_check, surface_array_definition plane_matched_surface_array)
        {
            int bsp2dnode_index = -1;
            int back_bsp2dnode_index = -1;
            int front_bsp2dnode_index = -1;
            if (plane_matched_surface_array.surface_array.Count == 1)
                return (int)(plane_matched_surface_array.surface_array[0] | 0x80000000);
            Bsp2dNode bsp2dnode_block = new Bsp2dNode(){LeftChild = -1, RightChild = -1};
            plane_splitting_parameters splitting_parameters = generate_best_splitting_plane_2D(plane_projection_axis, plane_mirror_check, ref bsp2dnode_block, plane_matched_surface_array);
            if(splitting_parameters.FrontSurfaceCount > 0 && splitting_parameters.BackSurfaceCount > 0)
            {
                surface_array_definition back_surface_array = new surface_array_definition {surface_array = new List<int>(new int[splitting_parameters.BackSurfaceCount]) };
                surface_array_definition front_surface_array = new surface_array_definition { surface_array = new List<int>(new int[splitting_parameters.FrontSurfaceCount]) };

                Bsp.Bsp2dNodes.Add(bsp2dnode_block);
                bsp2dnode_index = Bsp.Bsp2dNodes.Count - 1;

                sort_surfaces_by_plane_2D(splitting_parameters, plane_projection_axis, plane_mirror_check, bsp2dnode_block, plane_matched_surface_array, back_surface_array, front_surface_array);

                //create a child node with the back surface array first
                back_bsp2dnode_index = create_bsp2dnodes(plane_projection_axis, plane_mirror_check, back_surface_array);
                if (back_bsp2dnode_index == -1)
                {
                    bsp2dnode_index = -1;
                }
                else
                {
                    front_bsp2dnode_index = create_bsp2dnodes(plane_projection_axis, plane_mirror_check, front_surface_array);
                    if (front_bsp2dnode_index == -1)
                    {
                        bsp2dnode_index = -1;
                    }
                    else
                    {
                        //move the flag so that it won't get chopped off in the short cast
                        Bsp.Bsp2dNodes[bsp2dnode_index].LeftChild = back_bsp2dnode_index < 0 ? (short)(back_bsp2dnode_index | 0x8000) : (short)back_bsp2dnode_index;
                        Bsp.Bsp2dNodes[bsp2dnode_index].RightChild = front_bsp2dnode_index < 0 ? (short)(front_bsp2dnode_index | 0x8000) : (short)front_bsp2dnode_index;
                    }
                }
                return bsp2dnode_index;
            }
            Console.WriteLine("###ERROR couldn't build bsp because of overlapping surfaces.");
            return bsp2dnode_index;
        }

        public bool build_leaves(ref surface_array_definition surface_array, ref int leaf_index)
        {
            bool result = true;

            Bsp.Leaves.Add(new Leaf());
            leaf_index = Bsp.Leaves.Count - 1;

            //allocate initial leaf values
            Bsp.Leaves[leaf_index].Flags = 0;
            Bsp.Leaves[leaf_index].Bsp2dReferenceCount = 0;
            Bsp.Leaves[leaf_index].FirstBsp2dReference = 0xFFFFFFFF;

            for(int surface_array_index = 0; surface_array_index < surface_array.free_count + surface_array.used_count; surface_array_index++)
            {
                int surface_index = surface_array.surface_array[surface_array_index];
                Surface surface_block = Bsp.Surfaces[surface_index & 0x7FFFFFFF];

                //carry over surface flags
                if (surface_block.Flags.HasFlag(SurfaceFlags.TwoSided))
                    Bsp.Leaves[leaf_index].Flags |= LeafFlags.ContainsDoubleSidedSurfaces;

                if(surface_index < 0)
                {
                    int plane_index = (short)surface_block.Plane;
                    surface_array_definition plane_matched_surface_array = collect_plane_matching_surfaces(ref surface_array, plane_index);
                    if(plane_matched_surface_array.surface_array.Count > 0)
                    {
                        Bsp.Bsp2dReferences.Add(new Bsp2dReference());
                        int bsp2drefindex = Bsp.Bsp2dReferences.Count - 1;
                        Plane plane_block = Bsp.Planes[plane_index & 0x7FFF];
                        int plane_projection_axis = plane_determine_axis_minimum_coefficient(plane_block);
                        bool plane_projection_parameter_greater_than_0 = check_plane_projection_parameter_greater_than_0(plane_block, plane_projection_axis);
                        Bsp.Bsp2dReferences[bsp2drefindex].PlaneIndex = plane_index < 0 ? (short)(plane_index | 0x8000) : (short)plane_index;
                        Bsp.Bsp2dReferences[bsp2drefindex].Bsp2dNodeIndex = -1;

                        //update leaf block parameters given new bsp2dreference
                        Bsp.Leaves[leaf_index].Bsp2dReferenceCount++;
                        if (Bsp.Leaves[leaf_index].FirstBsp2dReference == 0xFFFFFFFF)
                            Bsp.Leaves[leaf_index].FirstBsp2dReference = (uint)bsp2drefindex;

                        //not 100% sure what this is checking
                        bool plane_index_negative = plane_index < 0;
                        int plane_mirror_check = plane_projection_parameter_greater_than_0 != plane_index_negative ? 1 : 0;

                        int bsp2dnodeindex = create_bsp2dnodes(plane_projection_axis, plane_mirror_check, plane_matched_surface_array);
                        Bsp.Bsp2dReferences[bsp2drefindex].Bsp2dNodeIndex = bsp2dnodeindex < 0 ? (short)(bsp2dnodeindex | 0x8000) : (short)bsp2dnodeindex;
                        if (bsp2dnodeindex == -1)
                        {
                            Console.WriteLine("###ERROR couldn't build bsp2d.");
                            result = false;
                        }
                    }
                }
            }
            return result;
        }

        public bool divide_surface_into_two_surfaces(int original_surface_index, int plane_index, int new_surface_index_A, int new_surface_index_B)
        {
            RealPlane3d plane_block = Bsp.Planes[plane_index].Value;
            Surface original_surface = Bsp.Surfaces[original_surface_index];

            //copy over values that won't change
            Bsp.Surfaces[new_surface_index_A].Flags = original_surface.Flags;
            Bsp.Surfaces[new_surface_index_A].BreakableSurfaceIndex = original_surface.BreakableSurfaceIndex;
            Bsp.Surfaces[new_surface_index_A].Plane = original_surface.Plane;
            Bsp.Surfaces[new_surface_index_A].FirstEdge = ushort.MaxValue; //0xFFFF
            Bsp.Surfaces[new_surface_index_B].Flags = original_surface.Flags;
            Bsp.Surfaces[new_surface_index_B].BreakableSurfaceIndex = original_surface.BreakableSurfaceIndex;
            Bsp.Surfaces[new_surface_index_B].Plane = original_surface.Plane;
            Bsp.Surfaces[new_surface_index_B].FirstEdge = ushort.MaxValue; //0xFFFF

            ushort surface_edge_index = original_surface.FirstEdge;

            //some debugging tools
            int total_edge_count = surface_count_edges(original_surface_index);
            List<Plane_Relationship> edge_plane_relationships = new List<Plane_Relationship>();

            int dividing_edge_index = -1;
            int previous_new_edge_index = -1;
            int first_new_edge_index = -1;
            int edge_vertex_A_index = -1;
            int edge_vertex_B_index = -1;

            //find edges of the original surface that are split by the plane and divide them
            while (true)
            {
                //grab edge vertices
                Edge surface_edge_block = Bsp.Edges[surface_edge_index];

                if (surface_edge_block.RightSurface == original_surface_index)
                {
                    edge_vertex_A_index = surface_edge_block.EndVertex;
                    edge_vertex_B_index = surface_edge_block.StartVertex;
                }
                else
                {
                    edge_vertex_A_index = surface_edge_block.StartVertex;
                    edge_vertex_B_index = surface_edge_block.EndVertex;
                }

                RealPoint3d edge_vertex_A = Bsp.Vertices[edge_vertex_A_index].Point;
                RealPoint3d edge_vertex_B = Bsp.Vertices[edge_vertex_B_index].Point;

                //calculate vertex plane relationships
                Plane_Relationship vertex_plane_relationship_A = determine_vertex_plane_relationship(edge_vertex_A, plane_block);
                Plane_Relationship vertex_plane_relationship_B = determine_vertex_plane_relationship(edge_vertex_B, plane_block);

                //check if there is a vertex on both sides of the plane
                Plane_Relationship edge_plane_relationship = vertex_plane_relationship_A | vertex_plane_relationship_B;
                edge_plane_relationships.Add(edge_plane_relationship);

                //if this edge spans the plane, then create a new dividing edge
                if(edge_plane_relationship.HasFlag(Plane_Relationship.BothSidesofPlane))
                {
                    //allocate new edges and vertex
                    Bsp.Vertices.Add(new Vertex());
                    int new_vertex_index_A = Bsp.Vertices.Count - 1;
                    Bsp.Edges.Add(new Edge());
                    Bsp.Edges.Add(new Edge());
                    int new_edge_index_A = Bsp.Edges.Count - 2;
                    int new_edge_index_B = Bsp.Edges.Count - 1;

                    if (dividing_edge_index == -1)
                    {
                        //allocate new dividing edge
                        Bsp.Edges.Add(new Edge());
                        dividing_edge_index = Bsp.Edges.Count - 1;

                        //new edge C will be the edge that separates the two new surfaces
                        Bsp.Edges[dividing_edge_index].StartVertex = (ushort)new_vertex_index_A;
                        Bsp.Edges[dividing_edge_index].ReverseEdge = (ushort)new_edge_index_B;
                        Bsp.Edges[dividing_edge_index].EndVertex = ushort.MaxValue; //0xFFFF
                        Bsp.Edges[dividing_edge_index].ForwardEdge = ushort.MaxValue; //0xFFFF
                        if (vertex_plane_relationship_A == Plane_Relationship.FrontofPlane || vertex_plane_relationship_A == Plane_Relationship.OnPlane)
                            Bsp.Edges[dividing_edge_index].LeftSurface = (ushort)new_surface_index_B;
                        else
                            Bsp.Edges[dividing_edge_index].LeftSurface = (ushort)new_surface_index_A;
                        if (vertex_plane_relationship_B == Plane_Relationship.FrontofPlane || vertex_plane_relationship_B == Plane_Relationship.OnPlane)
                            Bsp.Edges[dividing_edge_index].RightSurface = (ushort)new_surface_index_B;
                        else
                            Bsp.Edges[dividing_edge_index].RightSurface = (ushort)new_surface_index_A;
                    }
                    else
                    {
                        if(Bsp.Edges[dividing_edge_index].EndVertex != ushort.MaxValue)
                        {
                            Console.WriteLine("###ERROR Dividing Edge EndVertex should be -1");
                            return false;
                        }

                        Bsp.Edges[dividing_edge_index].EndVertex = (ushort)new_vertex_index_A;

                        if (Bsp.Edges[dividing_edge_index].ForwardEdge != ushort.MaxValue)
                        {
                            Console.WriteLine("###ERROR Dividing Edge ForwardEdge should be -1");
                            return false;
                        }

                        Bsp.Edges[dividing_edge_index].ForwardEdge = (ushort)new_edge_index_B;
                    }

                    float plane_vertex_input_A = edge_vertex_A.X * plane_block.I + edge_vertex_A.Y * plane_block.J + edge_vertex_A.Z * plane_block.K - plane_block.D;
                    float plane_vertex_input_B = edge_vertex_B.X * plane_block.I + edge_vertex_B.Y * plane_block.J + edge_vertex_B.Z * plane_block.K - plane_block.D;
                    float plane_vertex_input_AB_ratio = plane_vertex_input_A / (plane_vertex_input_A - plane_vertex_input_B);

                    //allocate values for new_vertex_A
                    Bsp.Vertices[new_vertex_index_A].Point.X = (edge_vertex_B.X - edge_vertex_A.X) * plane_vertex_input_AB_ratio + edge_vertex_A.X;
                    Bsp.Vertices[new_vertex_index_A].Point.Y = (edge_vertex_B.Y - edge_vertex_A.Y) * plane_vertex_input_AB_ratio + edge_vertex_A.Y;
                    Bsp.Vertices[new_vertex_index_A].Point.Z = (edge_vertex_B.Z - edge_vertex_A.Z) * plane_vertex_input_AB_ratio + edge_vertex_A.Z;
                    Bsp.Vertices[new_vertex_index_A].FirstEdge = (ushort)new_edge_index_A;

                    //allocate values for new_edge_A
                    Bsp.Edges[new_edge_index_A].ForwardEdge = (ushort)dividing_edge_index;
                    Bsp.Edges[new_edge_index_A].ReverseEdge = ushort.MaxValue; //0xFFFF
                    Bsp.Edges[new_edge_index_A].StartVertex = (ushort)edge_vertex_A_index;
                    Bsp.Edges[new_edge_index_A].EndVertex = (ushort)new_vertex_index_A;
                    if (vertex_plane_relationship_A == Plane_Relationship.FrontofPlane || vertex_plane_relationship_A == Plane_Relationship.OnPlane)
                        Bsp.Edges[new_edge_index_A].LeftSurface = (ushort)new_surface_index_B;
                    else
                        Bsp.Edges[new_edge_index_A].LeftSurface = (ushort)new_surface_index_A;
                    Bsp.Edges[new_edge_index_A].RightSurface = ushort.MaxValue; //0xFFFF

                    //allocate values for new_edge_B
                    Bsp.Edges[new_edge_index_B].ForwardEdge = ushort.MaxValue; //0xFFFF
                    Bsp.Edges[new_edge_index_B].ReverseEdge = ushort.MaxValue; //0xFFFF
                    Bsp.Edges[new_edge_index_B].StartVertex = (ushort)new_vertex_index_A;
                    Bsp.Edges[new_edge_index_B].EndVertex = (ushort)edge_vertex_B_index;
                    if (vertex_plane_relationship_B == Plane_Relationship.FrontofPlane || vertex_plane_relationship_B == Plane_Relationship.OnPlane)
                        Bsp.Edges[new_edge_index_B].LeftSurface = (ushort)new_surface_index_B;
                    else
                        Bsp.Edges[new_edge_index_B].LeftSurface = (ushort)new_surface_index_A;
                    Bsp.Edges[new_edge_index_B].RightSurface = ushort.MaxValue; //0xFFFF

                    if (first_new_edge_index == -1)
                        first_new_edge_index = new_edge_index_A;

                    //connect previous edge to generated edges
                    if (previous_new_edge_index != -1)
                        Bsp.Edges[previous_new_edge_index].ForwardEdge = (ushort)new_edge_index_A;
                    previous_new_edge_index = new_edge_index_B;
                }

                //not sure about what this is for, if vertex A isn't on the plane or vertex B IS on the plane...
                else if (vertex_plane_relationship_A != Plane_Relationship.OnPlane || vertex_plane_relationship_B == Plane_Relationship.OnPlane)
                {
                    //allocate new edge
                    Bsp.Edges.Add(new Edge());
                    int new_edge_index_D = Bsp.Edges.Count - 1;

                    Bsp.Edges[new_edge_index_D].StartVertex = (ushort)edge_vertex_A_index;
                    Bsp.Edges[new_edge_index_D].EndVertex = (ushort)edge_vertex_B_index;
                    Bsp.Edges[new_edge_index_D].ForwardEdge = ushort.MaxValue; //0xFFFF
                    Bsp.Edges[new_edge_index_D].ReverseEdge = ushort.MaxValue; //0xFFFF
                    if (edge_plane_relationship.HasFlag(Plane_Relationship.BackofPlane))
                        Bsp.Edges[new_edge_index_D].LeftSurface = (ushort)new_surface_index_A;
                    else
                        Bsp.Edges[new_edge_index_D].LeftSurface = (ushort)new_surface_index_B;
                    Bsp.Edges[new_edge_index_D].RightSurface = ushort.MaxValue; //0xFFFF

                    if (first_new_edge_index == -1)
                        first_new_edge_index = new_edge_index_D;

                    //connect previous edge to generated edges
                    if (previous_new_edge_index != -1)
                        Bsp.Edges[previous_new_edge_index].ForwardEdge = (ushort)new_edge_index_D;
                    previous_new_edge_index = new_edge_index_D;
                }

                else
                {
                    //allocate new edge
                    Bsp.Edges.Add(new Edge());
                    int new_edge_index_E = Bsp.Edges.Count - 1;

                    if (dividing_edge_index == -1)
                    {
                        //allocate new dividing edge
                        Bsp.Edges.Add(new Edge());
                        dividing_edge_index = Bsp.Edges.Count - 1;

                        Bsp.Edges[dividing_edge_index].StartVertex = (ushort)edge_vertex_A_index;
                        Bsp.Edges[dividing_edge_index].EndVertex = ushort.MaxValue; //0xFFFF
                        Bsp.Edges[dividing_edge_index].ForwardEdge = ushort.MaxValue; //0xFFFF
                        Bsp.Edges[dividing_edge_index].ReverseEdge = (ushort)new_edge_index_E;

                        if (vertex_plane_relationship_B == Plane_Relationship.BackofPlane)
                            Bsp.Edges[dividing_edge_index].LeftSurface = (ushort)new_surface_index_B;
                        else
                            Bsp.Edges[dividing_edge_index].LeftSurface = (ushort)new_surface_index_A;

                        if (vertex_plane_relationship_B == Plane_Relationship.FrontofPlane || vertex_plane_relationship_B == Plane_Relationship.OnPlane)
                            Bsp.Edges[dividing_edge_index].RightSurface = (ushort)new_surface_index_B;
                        else
                            Bsp.Edges[dividing_edge_index].RightSurface = (ushort)new_surface_index_A;
                    }
                    else
                    {
                        if (Bsp.Edges[dividing_edge_index].EndVertex != ushort.MaxValue)
                        {
                            Console.WriteLine("###ERROR Dividing Edge EndVertex should be -1");
                            return false;
                        }
                        Bsp.Edges[dividing_edge_index].EndVertex = (ushort)edge_vertex_A_index;
                        if (Bsp.Edges[dividing_edge_index].ForwardEdge != ushort.MaxValue)
                        {
                            Console.WriteLine("###ERROR Dividing Edge ForwardEdge should be -1");
                            return false;
                        }
                        Bsp.Edges[dividing_edge_index].ForwardEdge = (ushort)new_edge_index_E;
                    }

                    Bsp.Edges[new_edge_index_E].EndVertex = (ushort)edge_vertex_B_index;
                    Bsp.Edges[new_edge_index_E].StartVertex = (ushort)edge_vertex_A_index;
                    Bsp.Edges[new_edge_index_E].ForwardEdge = ushort.MaxValue; //0xFFFF
                    Bsp.Edges[new_edge_index_E].ReverseEdge = ushort.MaxValue; //0xFFFF
                    Bsp.Edges[new_edge_index_E].RightSurface = ushort.MaxValue; //0xFFFF

                    if (vertex_plane_relationship_B == Plane_Relationship.FrontofPlane || vertex_plane_relationship_B == Plane_Relationship.OnPlane)
                        Bsp.Edges[new_edge_index_E].LeftSurface = (ushort)new_surface_index_B;
                    else
                        Bsp.Edges[new_edge_index_E].LeftSurface = (ushort)new_surface_index_A;

                    if (first_new_edge_index == -1)
                        first_new_edge_index = dividing_edge_index;

                    //connect previous edge to new generated edges
                    if (previous_new_edge_index != -1)
                        Bsp.Edges[previous_new_edge_index].ForwardEdge = (ushort)dividing_edge_index;
                    previous_new_edge_index = new_edge_index_E;
                }

                if (surface_edge_block.RightSurface == original_surface_index)
                    surface_edge_index = surface_edge_block.ReverseEdge;
                else
                    surface_edge_index = surface_edge_block.ForwardEdge;
                //break the loop if we have finished circulating the surface
                if (surface_edge_index == original_surface.FirstEdge)
                {
                    //check for errors before exiting loop
                    if (first_new_edge_index == -1 || previous_new_edge_index == -1)
                    {
                        Console.WriteLine("###ERROR First New Edge index or Previous New Edge Index value not set!");
                        return false;
                    }
                    if (dividing_edge_index == -1)
                    {
                        Console.WriteLine("###ERROR Dividing Edge index value not set!");
                        return false;
                    }
                    break;
                }
            }
            //connect loose ends and set first edge of new surfaces
            Bsp.Edges[previous_new_edge_index].ForwardEdge = (ushort)first_new_edge_index;

            Bsp.Surfaces[new_surface_index_A].FirstEdge = (ushort)dividing_edge_index;
            Bsp.Surfaces[new_surface_index_B].FirstEdge = (ushort)dividing_edge_index;
            return true;
        }

        public bool split_object_surfaces_with_plane(surface_array_definition surface_array, int plane_index, ref surface_array_definition back_surfaces_array, ref surface_array_definition front_surfaces_array)
        {
            int surface_array_index = 0;

            int back_free_count = 0;
            int back_used_count = 0;
            int front_free_count = 0;
            int front_used_count = 0;

            int unknown_count = 0;
            int back_of_plane_count = 0;
            int front_of_plane_count = 0;
            int split_plane_count = 0;
            int on_plane_count = 0;

            if(surface_array.free_count + surface_array.used_count > 0)
            {
                while (true)
                {
                    int surface_index = surface_array.surface_array[surface_array_index];
                    bool surface_index_less_than_0 = false;
                    if (surface_index < 0)
                        surface_index_less_than_0 = true;
                    int absolute_surface_index = surface_index & 0x7FFFFFFF;
                    bool surface_is_free = surface_array_index < surface_array.free_count;
                    int current_surface_plane_index = (short)Bsp.Surfaces[absolute_surface_index].Plane;
                    bool surface_is_double_sided = Bsp.Surfaces[absolute_surface_index].Flags.HasFlag(SurfaceFlags.TwoSided);
                    int back_surface_index = -1;
                    int front_surface_index = -1;
                    switch (determine_surface_plane_relationship(absolute_surface_index, plane_index, new RealPlane3d()))
                    {
                        case Plane_Relationship.Unknown: //surface does not appear to be on either side of the plane and is not on the plane either
                            unknown_count++;
                            if (surface_index != -1)
                            {
                                if (surface_is_free)
                                {
                                    back_surfaces_array.surface_array[back_free_count] = surface_index;
                                    back_free_count++;
                                    front_surfaces_array.surface_array[front_free_count] = surface_index;
                                    front_free_count++;
                                }
                                else
                                {
                                    back_surfaces_array.surface_array[back_used_count + back_surfaces_array.free_count] = surface_index;
                                    back_used_count++;
                                    front_surfaces_array.surface_array[front_used_count + front_surfaces_array.free_count] = surface_index;
                                    front_used_count++;
                                }
                            }
                            break;

                        case Plane_Relationship.BackofPlane: //surface is in back of plane
                            back_of_plane_count++;
                            if (surface_index != -1)
                            {
                                if (surface_is_free)
                                {
                                    back_surfaces_array.surface_array[back_free_count] = surface_index;
                                    back_free_count++;
                                }
                                else
                                {
                                    back_surfaces_array.surface_array[back_used_count + back_surfaces_array.free_count] = surface_index;
                                    back_used_count++;
                                }
                            }
                            break;

                        case Plane_Relationship.FrontofPlane: //surface is in front of plane
                            front_of_plane_count++;
                            if (surface_index != -1)
                            {
                                if (surface_is_free)
                                {
                                    front_surfaces_array.surface_array[front_free_count] = surface_index;
                                    front_free_count++;
                                }
                                else
                                {
                                    front_surfaces_array.surface_array[front_used_count + front_surfaces_array.free_count] = surface_index;
                                    front_used_count++;
                                }
                            }
                            break;

                        case Plane_Relationship.BothSidesofPlane: //surface is both in front of and behind plane
                            split_plane_count++;
                            Bsp.Surfaces.Add(new Surface());
                            Bsp.Surfaces.Add(new Surface());
                            int new_surface_A_index = Bsp.Surfaces.Count - 2;
                            int new_surface_B_index = Bsp.Surfaces.Count - 1;
                           
                            //split surface into two new surfaces, one on each side of the plane
                            if(!divide_surface_into_two_surfaces(absolute_surface_index, plane_index, new_surface_A_index, new_surface_B_index))
                            {
                                return false;
                            }

                            //here we will record the parent surface index for these child surfaces for use later on
                            surface_addendums.Add(absolute_surface_index);
                            surface_addendums.Add(absolute_surface_index);

                            //propagate surface index flags to child surfaces
                            if (surface_index_less_than_0)
                            {
                                back_surface_index = (int)(new_surface_A_index | 0x80000000);
                                front_surface_index = (int)(new_surface_B_index | 0x80000000);
                            }
                            else
                            {
                                back_surface_index = (int)new_surface_A_index & 0x7FFFFFFF;
                                front_surface_index = (int)new_surface_B_index & 0x7FFFFFFF;
                            }

                            //add child surfaces to appropriate arrays
                            if (surface_is_free)
                            {
                                if (back_surface_index != -1)
                                {
                                    back_surfaces_array.surface_array[back_free_count] = back_surface_index;
                                    back_free_count++;
                                }
                                if (front_surface_index != -1)
                                {
                                    front_surfaces_array.surface_array[front_free_count] = front_surface_index;
                                    front_free_count++;
                                }
                            }
                            else
                            {
                                if (back_surface_index != -1)
                                {
                                    back_surfaces_array.surface_array[back_used_count + back_surfaces_array.free_count] = back_surface_index;
                                    back_used_count++;
                                }
                                if (front_surface_index != -1)
                                {
                                    front_surfaces_array.surface_array[front_used_count + front_surfaces_array.free_count] = front_surface_index;
                                    front_used_count++;
                                }
                            }
                            break;

                        case Plane_Relationship.OnPlane: //surface is ON the plane
                            on_plane_count++;
                            if (!surface_index_less_than_0)
                            {
                                break;
                            }
                            if(!surface_is_free || (current_surface_plane_index & 0x7FFF) != plane_index)
                            {
                                Console.WriteLine("###ERROR Surface on plane does not have matching plane index!");
                            }
                            if (!surface_is_double_sided) //surface is NOT double sided
                            {
                                if (current_surface_plane_index >= 0)
                                {
                                    back_surface_index = (int)absolute_surface_index & 0x7FFFFFFF;
                                    front_surface_index = (int)(absolute_surface_index | 0x80000000);
                                }
                                else
                                {
                                    back_surface_index = (int)(absolute_surface_index | 0x80000000);
                                    front_surface_index = (int)absolute_surface_index & 0x7FFFFFFF;
                                }
                            }
                            else
                            {
                                if (current_surface_plane_index >= 0)
                                {
                                    front_surface_index = (int)(absolute_surface_index | 0x80000000);
                                    back_surface_index = -1;
                                }
                                else
                                {
                                    back_surface_index = (int)(absolute_surface_index | 0x80000000);
                                    front_surface_index = -1;
                                }
                            }
                            //add child surfaces to appropriate arrays
                            //none of these surfaces are considered free, as surface_is_free is set to false for all conditions
                            if (back_surface_index != -1)
                            {
                                back_surfaces_array.surface_array[back_used_count + back_surfaces_array.free_count] = back_surface_index;
                                back_used_count++;
                            }
                            if (front_surface_index != -1)
                            {
                                front_surfaces_array.surface_array[front_used_count + front_surfaces_array.free_count] = front_surface_index;
                                front_used_count++;
                            }
                            break;
                    }

                    if (++surface_array_index >= surface_array.free_count + surface_array.used_count)
                        break;
                }
            }
            
            if (back_free_count != back_surfaces_array.free_count)
            {
                Console.WriteLine("###ERROR back_free_count != back_surfaces->free_count");
                return false;
            }
            if (back_used_count != back_surfaces_array.used_count)
            {
                Console.WriteLine("###ERROR back_used_count != back_surfaces->used_count");
                return false;
            }
            if (front_free_count != front_surfaces_array.free_count)
            {
                Console.WriteLine("###ERROR front_free_count != front_surfaces->free_count");
                return false;
            }
            if (front_used_count != front_surfaces_array.used_count)
            {
                Console.WriteLine("###ERROR front_used_count != front_surfaces->used_count");
                return false;
            }
            return true;
        }

        public plane_splitting_parameters find_surface_splitting_plane(surface_array_definition surface_array)
        {
            plane_splitting_parameters splitting_parameters = new plane_splitting_parameters();

            //this code should only be used with <1024 surfaces as it loops and checks every surface. large meshes first need to be split with generate_object_splitting_plane
            if (surface_array.free_count < 1024)
            {
                splitting_parameters = surface_plane_splitting_effectiveness_check_loop(surface_array);
                return splitting_parameters;
            }

            splitting_parameters = generate_object_splitting_plane(surface_array);
            return splitting_parameters;
        }

        [Flags]
        public enum Plane_Relationship : int
        {
            Unknown = 0,
            BackofPlane = 1,
            FrontofPlane = 2,
            BothSidesofPlane = 3, //both 1 & 2 
            OnPlane = 4
        }

        public Plane_Relationship determine_vertex_plane_relationship(RealPoint3d vertex, RealPlane3d plane)
        {
            double plane_equation_vertex_input = vertex.X * plane.I + vertex.Y * plane.J + vertex.Z * plane.K - plane.D;

            if (plane_equation_vertex_input >= -0.00024414062)
            {
                //if the plane distance is within both of these bounds, it is considered ON the plane
                if (plane_equation_vertex_input <= 0.00024414062)
                    return Plane_Relationship.OnPlane;
                else
                    return Plane_Relationship.FrontofPlane;
            }
            else
            {
                return Plane_Relationship.BackofPlane;
            }
        }

        public RealPoint2d vertex_get_projection_relevant_coords(Vertex vertex_block, int plane_projection_axis, int plane_mirror_check)
        {
            //plane mirror check = "projection_sign"
            RealPoint2d relevant_coords = new RealPoint2d();
            int v4 = 2 * (plane_mirror_check + 2 * plane_projection_axis);
            List<int> coordinate_list = new List<int> { 2, 1, 1, 2, 0, 2, 2, 0, 1, 0, 0, 1};
            int vertex_coord_A = coordinate_list[v4];
            int vertex_coord_B = coordinate_list[v4+1];
            switch (vertex_coord_A)
            {
                case 0:
                    relevant_coords.X = vertex_block.Point.X;
                    break;
                case 1:
                    relevant_coords.X = vertex_block.Point.Y;
                    break;
                case 2:
                    relevant_coords.X = vertex_block.Point.Z;
                    break;
            }
            switch (vertex_coord_B)
            {
                case 0:
                    relevant_coords.Y = vertex_block.Point.X;
                    break;
                case 1:
                    relevant_coords.Y = vertex_block.Point.Y;
                    break;
                case 2:
                    relevant_coords.Y = vertex_block.Point.Z;
                    break;
            }
            return relevant_coords;
        }

        public Plane_Relationship determine_surface_plane_relationship_2D(int surface_index, Bsp2dNode bsp2dnodeblock, int plane_projection_axis, int plane_mirror_check)
        {
            Plane_Relationship surface_plane_relationship = 0;
            Surface surface_block = Bsp.Surfaces[surface_index];
            List<RealPoint2d> pointlist = new List<RealPoint2d>();
            List<float> inputlist = new List<float>();

            int surface_edge_index = surface_block.FirstEdge;
            while (true)
            {
                Edge surface_edge_block = Bsp.Edges[surface_edge_index];
                Vertex edge_vertex;
                if (surface_edge_block.RightSurface == surface_index)
                    edge_vertex = Bsp.Vertices[surface_edge_block.EndVertex];
                else
                    edge_vertex = Bsp.Vertices[surface_edge_block.StartVertex];

                RealPoint2d relevant_coords = vertex_get_projection_relevant_coords(edge_vertex, plane_projection_axis, plane_mirror_check);
                pointlist.Add(relevant_coords);

                float plane_equation_vertex_input = bsp2dnodeblock.Plane.I * relevant_coords.X + bsp2dnodeblock.Plane.J * relevant_coords.Y - bsp2dnodeblock.Plane.D;
                inputlist.Add(plane_equation_vertex_input);

                if (plane_equation_vertex_input < -0.00012207031)
                {
                    surface_plane_relationship |= Plane_Relationship.BackofPlane;
                }
                if (plane_equation_vertex_input > 0.00012207031)
                {
                    surface_plane_relationship |= Plane_Relationship.FrontofPlane;
                }

                if (surface_plane_relationship.HasFlag(Plane_Relationship.BothSidesofPlane))
                    break;

                if (surface_edge_block.RightSurface == surface_index)
                    surface_edge_index = surface_edge_block.ReverseEdge;
                else
                    surface_edge_index = surface_edge_block.ForwardEdge;
                //break the loop if we have finished circulating the surface
                if (surface_edge_index == surface_block.FirstEdge)
                    break;
            }

            //check the surface is on at least one side of the plane
            if (surface_plane_relationship > 0)
                return surface_plane_relationship;

            if (debug)
            {
                Console.WriteLine("###WARNING found possible T-junction.");
                Console.WriteLine($"Plane:{bsp2dnodeblock.Plane}");
                foreach(var point in pointlist)
                    Console.WriteLine($"Vertex:{point}");
                foreach (var input in inputlist)
                    Console.WriteLine($"Plane Fit:{input}");
            }
            return surface_plane_relationship;
        }

        public Plane_Relationship determine_surface_plane_relationship(int surface_index, int plane_index, RealPlane3d plane_block)
        {
            Plane_Relationship surface_plane_relationship = 0;
            Surface surface_block = Bsp.Surfaces[surface_index];

            //check if surface is on the plane
            if ((surface_block.Plane & 0x7FFF) == plane_index)
                return Plane_Relationship.OnPlane;

            //if plane block is empty, use the plane index to get one instead
            if (plane_block == new RealPlane3d())
                plane_block = Bsp.Planes[plane_index].Value;

            int surface_edge_index = surface_block.FirstEdge;
            while (true)
            {
                Edge surface_edge_block = Bsp.Edges[surface_edge_index];
                RealPoint3d edge_vertex;
                if (surface_edge_block.RightSurface == surface_index)
                    edge_vertex = Bsp.Vertices[surface_edge_block.EndVertex].Point;
                else
                    edge_vertex = Bsp.Vertices[surface_edge_block.StartVertex].Point;

                Plane_Relationship vertex_plane_relationship = determine_vertex_plane_relationship(edge_vertex, plane_block);
                //if a vertex is on the plane, just ignore it and continue testing the remainder
                if (!vertex_plane_relationship.HasFlag(Plane_Relationship.OnPlane))
                    surface_plane_relationship |= vertex_plane_relationship;

                if (surface_plane_relationship.HasFlag(Plane_Relationship.BothSidesofPlane))
                    break;

                if (surface_edge_block.RightSurface == surface_index)
                    surface_edge_index = surface_edge_block.ReverseEdge;
                else
                    surface_edge_index = surface_edge_block.ForwardEdge;
                //break the loop if we have finished circulating the surface
                if (surface_edge_index == surface_block.FirstEdge)
                    break;
            }

            if(surface_index == 4417 && surface_plane_relationship == Plane_Relationship.Unknown)
            {
                Console.Write("yay");
            }

            //split surfaces may become very small and be hard to work with. To get clarity, we can use the parent surface of the split surface
            if(surface_plane_relationship == Plane_Relationship.Unknown && surface_index >= original_surface_count)
            {
                while (true)
                {
                    surface_index = surface_addendums[surface_index];
                    surface_plane_relationship = determine_surface_plane_relationship(surface_index, plane_index, plane_block);
                    if (surface_plane_relationship == Plane_Relationship.BackofPlane || surface_plane_relationship == Plane_Relationship.FrontofPlane)
                        break;
                    if (surface_index < original_surface_count)
                    {
                        surface_plane_relationship = Plane_Relationship.Unknown;
                        break;
                    }                       
                }          
            }

            return surface_plane_relationship;
        }

        public class plane_splitting_parameters
        {
            public double plane_splitting_effectiveness;
            public int plane_index;
            public int BackSurfaceCount;
            public int BackSurfaceUsedCount;
            public int FrontSurfaceCount;
            public int FrontSurfaceUsedCount;
        }

        public class surface_array_definition
        {
            public int free_count;
            public int used_count;
            public List<int> surface_array;
        }

        public plane_splitting_parameters determine_plane_splitting_effectiveness_2D(Bsp2dNode bsp2dnode_block, int plane_projection_axis, int a3, surface_array_definition plane_matched_surface_array)
        {
            plane_splitting_parameters splitting_Parameters = new plane_splitting_parameters();
            foreach(int surface_index in plane_matched_surface_array.surface_array)
            {
                Plane_Relationship surface_plane_orientation = determine_surface_plane_relationship_2D(surface_index, bsp2dnode_block, plane_projection_axis, a3);
                if (surface_plane_orientation.HasFlag(Plane_Relationship.BackofPlane))
                    splitting_Parameters.BackSurfaceCount++;
                if (surface_plane_orientation.HasFlag(Plane_Relationship.FrontofPlane))
                    splitting_Parameters.FrontSurfaceCount++;
            }
            //if all of the surfaces are on one side or the other, this is not a good split
            if (splitting_Parameters.BackSurfaceCount >= plane_matched_surface_array.surface_array.Count || splitting_Parameters.FrontSurfaceCount >= plane_matched_surface_array.surface_array.Count)
                splitting_Parameters.plane_splitting_effectiveness = double.MaxValue;
            else
                splitting_Parameters.plane_splitting_effectiveness =
                Math.Abs(splitting_Parameters.BackSurfaceCount - splitting_Parameters.FrontSurfaceCount) + 2 * (splitting_Parameters.FrontSurfaceCount + splitting_Parameters.BackSurfaceCount);

            return splitting_Parameters;
        }

        public plane_splitting_parameters determine_plane_splitting_effectiveness(surface_array_definition surface_array, int plane_index, RealPlane3d plane_block)
        {
            plane_splitting_parameters splitting_Parameters = new plane_splitting_parameters();
            if (surface_array.free_count + surface_array.used_count > 0)
            {
                int current_surface_array_index = 0;
                while (true)
                {
                    int surface_index = surface_array.surface_array[current_surface_array_index];
                    bool surface_is_free = current_surface_array_index < surface_array.free_count;
                    Plane_Relationship relationship = determine_surface_plane_relationship((surface_index & 0x7FFFFFFF), plane_index, plane_block);
                    if (relationship.HasFlag(Plane_Relationship.OnPlane))
                    {
                        if (!surface_is_free)
                        {
                            Console.WriteLine("###ERROR Plane matching surface should be free!");
                        }
                        if(surface_index < 0)
                        {
                            Surface surface_block = Bsp.Surfaces[surface_index & 0x7FFFFFFF];
                            if (surface_block.Flags.HasFlag(SurfaceFlags.TwoSided))
                            {
                                if((surface_block.Plane & 0x8000) > 0)
                                {
                                    splitting_Parameters.BackSurfaceUsedCount++;
                                    if (++current_surface_array_index >= surface_array.free_count + surface_array.used_count)
                                        break;
                                    continue;
                                }
                            }
                            else
                            {
                                splitting_Parameters.BackSurfaceUsedCount++;
                            }
                            splitting_Parameters.FrontSurfaceUsedCount++;
                            if (++current_surface_array_index >= surface_array.free_count + surface_array.used_count)
                                break;
                            continue;
                        }
                    }
                    else
                    {
                        //if surface seems to be on neither side of the plane, consider it to be on both
                        if (!relationship.HasFlag(Plane_Relationship.BackofPlane) && !relationship.HasFlag(Plane_Relationship.FrontofPlane))
                        {
                            relationship |= Plane_Relationship.FrontofPlane;
                            relationship |= Plane_Relationship.BackofPlane;
                        }
                        if (surface_is_free)
                        {
                            if (relationship.HasFlag(Plane_Relationship.BackofPlane))
                                splitting_Parameters.BackSurfaceCount++;
                            if (relationship.HasFlag(Plane_Relationship.FrontofPlane))
                                splitting_Parameters.FrontSurfaceCount++;
                        }
                        else
                        {
                            if (relationship.HasFlag(Plane_Relationship.BackofPlane))
                                splitting_Parameters.BackSurfaceUsedCount++;
                            if (relationship.HasFlag(Plane_Relationship.FrontofPlane))
                                splitting_Parameters.FrontSurfaceUsedCount++;
                        }
                    }

                    if (++current_surface_array_index >= surface_array.free_count + surface_array.used_count)
                        break;
                }
            }

            splitting_Parameters.plane_splitting_effectiveness =
                Math.Abs(splitting_Parameters.BackSurfaceCount - splitting_Parameters.FrontSurfaceCount) + 2 * (splitting_Parameters.FrontSurfaceCount + splitting_Parameters.BackSurfaceCount);

            return splitting_Parameters;
        }

        public Bsp2dNode generate_bsp2d_plane_parameters(RealPoint2d Coords1, RealPoint2d Coords2)
        {
            Bsp2dNode bsp2dnode_block = new Bsp2dNode();
            double plane_I = Coords2.Y - Coords1.Y;
            double plane_J = Coords1.X - Coords2.X;

            double dist = (float)Math.Sqrt(plane_I * plane_I + plane_J * plane_J);

            if (Math.Abs(dist) < 0.0001)
            {
                bsp2dnode_block.Plane.I = (float)plane_I;
                bsp2dnode_block.Plane.J = (float)plane_J;
                bsp2dnode_block.Plane.D = 0;
            }
            else
            {
                bsp2dnode_block.Plane.I = (float)(plane_I / dist);
                bsp2dnode_block.Plane.J = (float)(plane_J / dist);
                bsp2dnode_block.Plane.D = bsp2dnode_block.Plane.J * Coords1.Y + bsp2dnode_block.Plane.I * Coords1.X;
            }
            return bsp2dnode_block;
        }

        public plane_splitting_parameters generate_best_splitting_plane_2D(int plane_projection_axis, int plane_mirror_check, ref Bsp2dNode bsp2dnode_block, surface_array_definition plane_matched_surface_array)
        {
            plane_splitting_parameters lowest_plane_splitting_parameters = new plane_splitting_parameters();
            lowest_plane_splitting_parameters.plane_splitting_effectiveness = double.MaxValue;

            for(int surface_index_index = 0; surface_index_index < plane_matched_surface_array.surface_array.Count; surface_index_index++)
            {
                int surface_index = plane_matched_surface_array.surface_array[surface_index_index] & 0x7FFFFFFF;
                Surface surface_block = Bsp.Surfaces[surface_index];
                int surface_edge_index = surface_block.FirstEdge;
                while (true)
                {
                    Edge surface_edge_block = Bsp.Edges[surface_edge_index];
                    Vertex start_vertex = Bsp.Vertices[surface_edge_block.StartVertex];
                    Vertex end_vertex = Bsp.Vertices[surface_edge_block.EndVertex];
                    RealPoint2d Coords1;
                    RealPoint2d Coords2;
                    if (surface_edge_block.RightSurface == surface_index)
                    {
                        Coords1 = vertex_get_projection_relevant_coords(end_vertex, plane_projection_axis, plane_mirror_check);
                        Coords2 = vertex_get_projection_relevant_coords(start_vertex, plane_projection_axis, plane_mirror_check);
                    }
                    else
                    {
                        Coords2 = vertex_get_projection_relevant_coords(end_vertex, plane_projection_axis, plane_mirror_check);
                        Coords1 = vertex_get_projection_relevant_coords(start_vertex, plane_projection_axis, plane_mirror_check);
                    }

                    Bsp2dNode current_bsp2dnode_block = generate_bsp2d_plane_parameters(Coords1, Coords2);

                    plane_splitting_parameters current_plane_splitting_parameters = determine_plane_splitting_effectiveness_2D(current_bsp2dnode_block, plane_projection_axis, plane_mirror_check, plane_matched_surface_array);

                    if(current_plane_splitting_parameters.plane_splitting_effectiveness < lowest_plane_splitting_parameters.plane_splitting_effectiveness)
                    {
                        lowest_plane_splitting_parameters = current_plane_splitting_parameters.DeepClone();
                        bsp2dnode_block = current_bsp2dnode_block.DeepClone();
                    }

                    if (surface_edge_block.RightSurface == surface_index)
                        surface_edge_index = surface_edge_block.ReverseEdge;
                    else
                        surface_edge_index = surface_edge_block.ForwardEdge;
                    //break the loop if we have finished circulating the surface
                    if (surface_edge_index == surface_block.FirstEdge)
                        break;
                }
            }
            return lowest_plane_splitting_parameters;
        }

        public plane_splitting_parameters surface_plane_splitting_effectiveness_check_loop(surface_array_definition surface_array)
        {
            plane_splitting_parameters lowest_plane_splitting_parameters = new plane_splitting_parameters();
            lowest_plane_splitting_parameters.plane_splitting_effectiveness = double.MaxValue;

            //loop through free surfaces to see how effectively their associated planes split the remaining surfaces. Find the one that most effectively splits the remaining surfaces.
            for (int i = 0; i < surface_array.free_count; i++)
            {
                int current_plane_index = (int)(Bsp.Surfaces[(surface_array.surface_array[i] & 0x7FFFFFFF)].Plane & 0x7FFF);
                plane_splitting_parameters current_plane_splitting_parameters = determine_plane_splitting_effectiveness(surface_array, current_plane_index, new RealPlane3d());
                if(current_plane_splitting_parameters.plane_splitting_effectiveness < lowest_plane_splitting_parameters.plane_splitting_effectiveness)
                {
                    lowest_plane_splitting_parameters = current_plane_splitting_parameters.DeepClone();
                    lowest_plane_splitting_parameters.plane_index = current_plane_index;
                }
            }

            return lowest_plane_splitting_parameters;
        }

        public class surface_array_qsort_compar: IComparer<extent_entry>
        {
            public int Compare(extent_entry element1, extent_entry element2)
            {
                if (element1.coordinate < element2.coordinate)
                    return -1;
                if (element1.coordinate > element2.coordinate)
                    return 1;
                if (element2.is_max_coord && !element1.is_max_coord)
                    return -1;
                if (element1.is_max_coord && !element2.is_max_coord)
                    return 1;
                return 0;
            }
        }

        public class extent_entry
        {
            public float coordinate;
            public bool is_max_coord;
        };

        public plane_splitting_parameters generate_object_splitting_plane(surface_array_definition surface_array)
        {
            plane_splitting_parameters lowest_plane_splitting_parameters = new plane_splitting_parameters();
            lowest_plane_splitting_parameters.plane_splitting_effectiveness = double.MaxValue;

            int best_axis_index = -1;
            double desired_plane_distance = 0;

            //check X, Y, and Z axes
            for (int current_test_axis = 0; current_test_axis < 3; current_test_axis++)
            {
                //generate a list of maximum and minimum coordinates on the specified plane for each surface
                List<extent_entry> extents_table = new List<extent_entry>();
                for (int i = 0; i < surface_array.free_count; i++)
                {
                    extent_entry min_coordinate = new extent_entry { coordinate = float.MaxValue, is_max_coord = false };
                    extent_entry max_coordinate = new extent_entry { coordinate = float.MinValue, is_max_coord = true };
                    int surface_index = surface_array.surface_array[i] & 0x7FFFFFFF;
                    Surface surface_block = Bsp.Surfaces[surface_index];
                    int surface_edge_index = surface_block.FirstEdge;
                    while (true)
                    {
                        Edge surface_edge_block = Bsp.Edges[surface_edge_index];
                        RealPoint3d edge_vertex;
                        if (surface_edge_block.RightSurface == surface_index)
                            edge_vertex = Bsp.Vertices[surface_edge_block.EndVertex].Point;
                        else
                            edge_vertex = Bsp.Vertices[surface_edge_block.StartVertex].Point;
                        switch (current_test_axis)
                        {
                            case 0: //X axis
                                if (min_coordinate.coordinate > edge_vertex.X)
                                    min_coordinate.coordinate = edge_vertex.X;
                                if (max_coordinate.coordinate < edge_vertex.X)
                                    max_coordinate.coordinate = edge_vertex.X;
                                break;
                            case 1: //Y axis
                                if (min_coordinate.coordinate > edge_vertex.Y)
                                    min_coordinate.coordinate = edge_vertex.Y;
                                if (max_coordinate.coordinate < edge_vertex.Y)
                                    max_coordinate.coordinate = edge_vertex.Y;
                                break;
                            case 2: //Z axis
                                if (min_coordinate.coordinate > edge_vertex.Z)
                                    min_coordinate.coordinate = edge_vertex.Z;
                                if (max_coordinate.coordinate < edge_vertex.Z)
                                    max_coordinate.coordinate = edge_vertex.Z;
                                break;
                        }

                        if (surface_edge_block.RightSurface == surface_index)
                            surface_edge_index = surface_edge_block.ReverseEdge;
                        else
                            surface_edge_index = surface_edge_block.ForwardEdge;
                        //break the loop if we have finished circulating the surface
                        if (surface_edge_index == surface_block.FirstEdge)
                            break;
                    }
                    extents_table.Add(min_coordinate);
                    extents_table.Add(max_coordinate);
                }
                //use the above defined comparer to sort the extent entries first by coordinate and then by whether they are a surface max coordinate or not
                surface_array_qsort_compar sorter = new surface_array_qsort_compar();
                extents_table.Sort(sorter);

                int front_count = surface_array.free_count;
                int back_count = 0;
                for (int extent_index = 1; extent_index < extents_table.Count; extent_index++)
                {
                    if (extents_table[extent_index - 1].is_max_coord)
                    {
                        if (--front_count < 0)
                        {
                            Console.WriteLine("###ERROR negative plane front count!");
                        }
                    }
                    else
                    {
                        if(++back_count > surface_array.free_count)
                        {
                            Console.WriteLine("###ERROR back count greater than surface free count!");
                        }
                    }
                    double current_splitting_effectiveness = Math.Abs(back_count - front_count) + 2 * (front_count + back_count);
                    if (current_splitting_effectiveness < lowest_plane_splitting_parameters.plane_splitting_effectiveness)
                    {
                        lowest_plane_splitting_parameters.plane_splitting_effectiveness = current_splitting_effectiveness;
                        best_axis_index = current_test_axis;
                        desired_plane_distance = (extents_table[extent_index].coordinate + extents_table[extent_index - 1].coordinate) * 0.5;
                    }
                }
            }
            if(best_axis_index == -1)
            {
                Console.WriteLine("###ERROR: Failed to find best axis index!");
            }
            //generate a plane based on the ideal plane characteristics calculated above
            RealPlane3d best_plane = new RealPlane3d();
            best_plane.D = (float)desired_plane_distance;
            switch (best_axis_index)
            {
                case 0:
                    best_plane.I = 1.0f;
                    break;
                case 1:
                    best_plane.J = 1.0f;
                    break;
                case 2:
                    best_plane.K = 1.0f;
                    break;
            }
            //an identical but opposite facing plane can also be used
            RealPlane3d inverse_best_plane = new RealPlane3d
            {
                I = best_plane.I * -1,
                J = best_plane.J * -1,
                K = best_plane.K * -1,
                D = best_plane.D * -1,
            };
            //we can use an existing plane if it is within this tolerance threshold 
            double plane_tolerance = 0.001000000047497451;
            int ideal_plane_index;
            for(ideal_plane_index = 0; ideal_plane_index < Bsp.Planes.Count; ideal_plane_index++)
            {
                var plane = Bsp.Planes[ideal_plane_index].Value;
                if(Math.Abs(plane.I - best_plane.I) < plane_tolerance &&
                    Math.Abs(plane.J - best_plane.J) < plane_tolerance &&
                    Math.Abs(plane.K - best_plane.K) < plane_tolerance &&
                    Math.Abs(plane.D - best_plane.D) < plane_tolerance)
                {
                    break;
                }
                else if (Math.Abs(plane.I - inverse_best_plane.I) < plane_tolerance &&
                    Math.Abs(plane.J - inverse_best_plane.J) < plane_tolerance &&
                    Math.Abs(plane.K - inverse_best_plane.K) < plane_tolerance &&
                    Math.Abs(plane.D - inverse_best_plane.D) < plane_tolerance)
                {
                    break;
                }
            }
            //if we have found a usable existing plane, use it
            if(ideal_plane_index < Bsp.Planes.Count)
            {
                lowest_plane_splitting_parameters = determine_plane_splitting_effectiveness(surface_array, ideal_plane_index, new RealPlane3d());
                lowest_plane_splitting_parameters.plane_index = ideal_plane_index;
            }
            //otherwise just add a new plane with ideal characteristics
            else
            {
                Bsp.Planes.Add(new Plane { Value = best_plane });
                ideal_plane_index = Bsp.Planes.Count - 1;
                lowest_plane_splitting_parameters = determine_plane_splitting_effectiveness(surface_array, ideal_plane_index, new RealPlane3d());
                lowest_plane_splitting_parameters.plane_index = ideal_plane_index;
            }
            return lowest_plane_splitting_parameters;
        }

        ///////////////////////////////////////////
        //bsp/geo reduction stuff stored down here
        ///////////////////////////////////////////
       
        public bool reduce_collision_bsp() //function unfinished, not needed yet
        {
            List<int> deleted_surface_array = new List<int>(new int[Bsp.Surfaces.Count]);
            List<int> deleted_edge_array = new List<int>(new int[Bsp.Edges.Count]);
            List<int> deleted_vertex_array = new List<int>(new int[Bsp.Vertices.Count]);

            //make a list of valid and invalid surfaces
            int new_surface_index = 0;
            for (int surface_index = 0; surface_index < Bsp.Surfaces.Count; surface_index++)
            {
                deleted_surface_array[surface_index] = new_surface_index++;
            }

            //make a list of valid and invalid edges
            int new_edge_index = 0;
            for (int edge_index = 0; edge_index < Bsp.Edges.Count; edge_index++)
            {
                bool edge_is_valid = false;
                if (Bsp.Edges[edge_index].LeftSurface != ushort.MaxValue)
                {
                    if (deleted_surface_array[Bsp.Edges[edge_index].LeftSurface] != -1)
                        edge_is_valid = true;
                }
                else if (Bsp.Edges[edge_index].RightSurface != ushort.MaxValue)
                {
                    if (deleted_surface_array[Bsp.Edges[edge_index].RightSurface] != -1)
                        edge_is_valid = true;
                }
                else if(edge_is_valid)
                    deleted_edge_array[edge_index] = new_edge_index++;
                else
                    deleted_edge_array[edge_index] = -1;
            }

            //make a list of valid and invalid vertices
            int new_vertex_index = 0;
            for (int vertex_index = 0; vertex_index < Bsp.Vertices.Count; vertex_index++)
            {
                bool vertex_is_valid = true;
                ushort current_edge_index = Bsp.Vertices[vertex_index].FirstEdge;
                while (true)
                {
                    Edge edge_block = Bsp.Edges[current_edge_index];
                    if (deleted_edge_array[Bsp.Vertices[vertex_index].FirstEdge] != -1)
                        break;
                    if (edge_block.EndVertex != vertex_index)
                        current_edge_index = edge_block.ReverseEdge;
                    else
                        current_edge_index = edge_block.ForwardEdge;
                    if (current_edge_index == Bsp.Vertices[vertex_index].FirstEdge || current_edge_index == ushort.MaxValue)
                    {
                        current_edge_index = 0;
                        if (Bsp.Edges.Count <= 0)
                        {
                            deleted_vertex_array[vertex_index] = -1;
                            vertex_is_valid = false;
                        }
                        while (true)
                        {
                            if (deleted_edge_array[current_edge_index] != -1)
                            {
                                Edge test_edge = Bsp.Edges[current_edge_index];
                                if (test_edge.StartVertex == vertex_index || test_edge.EndVertex == vertex_index)
                                {
                                    break;
                                }
                            }
                            if (++current_edge_index >= Bsp.Edges.Count)
                            {
                                deleted_vertex_array[vertex_index] = -1;
                                vertex_is_valid = false;
                                break;
                            }
                        }
                        break;
                    }
                }
                if (vertex_is_valid)
                {
                    Bsp.Vertices[vertex_index].FirstEdge = (ushort)current_edge_index;
                    deleted_vertex_array[vertex_index] = new_vertex_index++;
                }
            }

            //INDEX REFERENCE FIXUPS BEGIN HERE

            if (Bsp.Bsp2dReferences.Count > 0)
            {
                for (int bsp2dref_index = 0; bsp2dref_index < Bsp.Bsp2dReferences.Count; bsp2dref_index++)
                {
                    short bsp2dnode_index = Bsp.Bsp2dReferences[bsp2dref_index].Bsp2dNodeIndex;
                    if (bsp2dnode_index < 0)
                    {
                        int absolute_bsp2dnode_index = bsp2dnode_index & 0x7FFF;
                        Bsp.Bsp2dReferences[bsp2dref_index].Bsp2dNodeIndex = (short)(deleted_surface_array[absolute_bsp2dnode_index] | 0x8000);
                    }
                }
            }

            //bsp2dnode children that have the 0x8000 flag actually encode surface indices, not other bsp2dnodes
            if (Bsp.Bsp2dNodes.Count > 0)
            {
                for (int bsp2dnode_index = 0; bsp2dnode_index < Bsp.Bsp2dReferences.Count; bsp2dnode_index++)
                {
                    short leftchild_index = Bsp.Bsp2dNodes[bsp2dnode_index].LeftChild;
                    if (leftchild_index < 0)
                    {
                        int absolute_leftchild_index = leftchild_index & 0x7FFF;
                        Bsp.Bsp2dNodes[bsp2dnode_index].LeftChild = (short)(deleted_surface_array[absolute_leftchild_index] | 0x8000);
                    }

                    short rightchild_index = Bsp.Bsp2dNodes[bsp2dnode_index].RightChild;
                    if (rightchild_index < 0)
                    {
                        int absolute_rightchild_index = rightchild_index & 0x7FFF;
                        Bsp.Bsp2dNodes[bsp2dnode_index].RightChild = (short)(deleted_surface_array[absolute_rightchild_index] | 0x8000);
                    }
                }
            }

            for (int surface_index = 0; surface_index < Bsp.Surfaces.Count; surface_index++)
            {
                if (deleted_surface_array[surface_index] != -1)
                {
                    Bsp.Surfaces[surface_index].FirstEdge = (ushort)deleted_edge_array[Bsp.Surfaces[surface_index].FirstEdge];
                }
            }

            for (int edge_index = 0; edge_index < Bsp.Edges.Count; edge_index++)
            {
                if (deleted_edge_array[edge_index] != -1)
                {
                    Bsp.Edges[edge_index].StartVertex = (ushort)deleted_vertex_array[Bsp.Edges[edge_index].StartVertex];
                    Bsp.Edges[edge_index].EndVertex = (ushort)deleted_vertex_array[Bsp.Edges[edge_index].EndVertex];
                    if (Bsp.Edges[edge_index].ForwardEdge != ushort.MaxValue)
                        Bsp.Edges[edge_index].ForwardEdge = (ushort)deleted_edge_array[Bsp.Edges[edge_index].ForwardEdge];
                    Bsp.Edges[edge_index].ReverseEdge = (ushort)deleted_edge_array[Bsp.Edges[edge_index].ReverseEdge];
                }
            }

            return true;
        }

        //////////////////////////
        //VERIFICATION CHECKS
        ///////////////////////////
        public bool verify_collision_geometry()
        {
            if(Bsp.Edges.Count > 0 && Bsp.Surfaces.Count > 0)
            {
                for(int edge_index = 0; edge_index < Bsp.Edges.Count; edge_index++)
                {
                    Edge edge_block = Bsp.Edges[edge_index];
                    if(edge_block.StartVertex < 0 || edge_block.StartVertex >= Bsp.Vertices.Count)
                    {
                        Console.WriteLine($"###ERROR edge {edge_index} has a bad start vertex index.");
                        return false;
                    }
                    if (edge_block.EndVertex < 0 || edge_block.EndVertex >= Bsp.Vertices.Count)
                    {
                        Console.WriteLine($"###ERROR edge {edge_index} has a bad end vertex index.");
                        return false;
                    }
                    if(edge_block.StartVertex == edge_block.EndVertex)
                    {
                        Console.WriteLine($"###ERROR edge {edge_index} references only one vertex.");
                        return false;
                    }
                    if(edge_get_length(edge_index) < 0.001)
                    {
                        Console.WriteLine($"###ERROR edge {edge_index} is too short.");
                        return false;
                    }

                    if (edge_block.ForwardEdge < 0 || edge_block.ForwardEdge >= Bsp.Edges.Count)
                    {
                        Console.WriteLine($"###ERROR edge {edge_index} has a bad forward edge index.");
                        return false;
                    }
                    if (edge_block.ReverseEdge < 0 || edge_block.ReverseEdge >= Bsp.Edges.Count)
                    {
                        Console.WriteLine($"###ERROR edge {edge_index} has a bad reverse edge index.");
                        return false;
                    }
                    if (edge_block.ForwardEdge == edge_index || edge_block.ReverseEdge == edge_index)
                    {
                        Console.WriteLine($"###ERROR edge {edge_index} references itself.");
                        return false;
                    }
                    if (edge_block.ForwardEdge == edge_block.ReverseEdge)
                    {
                        Console.WriteLine($"###ERROR edge {edge_index} references only one edge.");
                        return false;
                    }
                    for(int direction = 0; direction < 2; direction++)
                    {
                        int next_edge_index = direction == 0 ? edge_block.ForwardEdge : edge_block.ReverseEdge;
                        Edge next_edge_block = Bsp.Edges[next_edge_index];
                        int test_vertex = direction == 0 ? edge_block.EndVertex : edge_block.StartVertex;
                        int test_surface = direction == 0 ? edge_block.LeftSurface : edge_block.RightSurface;
                        if ((next_edge_block.StartVertex != test_vertex || next_edge_block.LeftSurface != test_surface)
                         && (next_edge_block.EndVertex != test_vertex || next_edge_block.RightSurface != test_surface))
                        {
                            string directionstring = direction == 0 ? "forward" : "reverse";
                            Console.WriteLine($"###ERROR edge {edge_index} doesn't share a vertex or surface with its {directionstring} edge.");
                            return false;
                        }
                    }
                    if (edge_block.LeftSurface < 0 || edge_block.LeftSurface >= Bsp.Surfaces.Count)
                    {
                        Console.WriteLine($"###ERROR edge {edge_index} has a bad left surface index.");
                        return false;
                    }
                    if (edge_block.RightSurface < 0 || edge_block.RightSurface >= Bsp.Surfaces.Count)
                    {
                        Console.WriteLine($"###ERROR edge {edge_index} has a bad right surface index.");
                        return false;
                    }
                    if (edge_block.LeftSurface == edge_block.RightSurface)
                    {
                        Console.WriteLine($"###ERROR edge {edge_index} references only one surface.");
                        return false;
                    }
                }
                for (int surface_index = 0; surface_index < Bsp.Surfaces.Count; surface_index++)
                {
                    Surface surface_block = Bsp.Surfaces[surface_index];
                    if(surface_block.FirstEdge < 0 || surface_block.FirstEdge >= Bsp.Edges.Count)
                    {
                        Console.WriteLine($"###ERROR surface {surface_index} has a bad first edge index");
                        return false;
                    }
                    if(surface_count_edges(surface_index) > 8)
                    {
                        Console.WriteLine($"###ERROR surface {surface_index} has too many edges");
                        return false;
                    }
                }
            }
            else
            {
                return false;
            }
            return true;
        }

        public float edge_get_length(int edge_index)
        {
            Edge target_edge = Bsp.Edges[edge_index];
            RealPoint3d start_vertex = Bsp.Vertices[target_edge.StartVertex].Point;
            RealPoint3d end_vertex = Bsp.Vertices[target_edge.EndVertex].Point;
            double xdiff = end_vertex.X - start_vertex.X;
            double ydiff = end_vertex.Y - start_vertex.Y;
            double zdiff = end_vertex.Z - start_vertex.Z;
            return (float)Math.Sqrt(xdiff * xdiff + zdiff * zdiff + ydiff * ydiff);
        }

        int surface_count_edges(int surface_index)
        {
            int edge_count = 0;
            Surface surface_block = Bsp.Surfaces[surface_index];
            int first_Edge_index = surface_block.FirstEdge;
            int current_edge_index = surface_block.FirstEdge;
            do
            {
                Edge edge_block = Bsp.Edges[current_edge_index];
                ++edge_count;
                if (edge_block.RightSurface == surface_index)
                    current_edge_index = edge_block.ReverseEdge;
                else
                    current_edge_index = edge_block.ForwardEdge;
            }
            while (current_edge_index != first_Edge_index);
            return edge_count;
        }
    }
}