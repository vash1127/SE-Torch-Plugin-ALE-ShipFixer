﻿using NLog;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System.Collections.Concurrent;
using VRage.Groups;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;
using IMyShipController = Sandbox.ModAPI.IMyShipController;
using IMyTerminalBlock = Sandbox.ModAPI.Ingame.IMyTerminalBlock;
using Torch;
using Torch.API;
using VRage.Game.ModAPI;
using System.Collections.Generic;
using Torch.Commands;
using System.IO;
using System;
using System.Linq;
using Torch.API.Plugins;
using System.Windows.Controls;
using VRage.Game;
using System.Threading.Tasks;
using ALE_Core.Utils;
using Sandbox.Common.ObjectBuilders;
using ALE_Core.Cooldown;
using Sandbox.Game.World;

namespace ALE_ShipFixer {

    public class ShipFixerPlugin : TorchPluginBase, IWpfPlugin {

        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private Control _control;
        public UserControl GetControl() => _control ?? (_control = new Control(this));

        private Persistent<ShipFixerConfig> _config;
        public ShipFixerConfig Config => _config?.Data;

        public CooldownManager CommandCooldownManager { get; } = new CooldownManager();
        public CooldownManager ConfirmationCooldownManager { get; } = new CooldownManager();

        public long Cooldown { get { return Config.CooldownInSeconds * 1000; } }
        public long CooldownConfirmationSeconds { get { return Config.ConfirmationInSeconds; } }
        public long CooldownConfirmation { get { return Config.ConfirmationInSeconds * 1000; } }
        public bool PlayerCommandEnabled { get { return Config.PlayerCommandEnabled; } }
        public bool FactionFixEnabled { get { return Config.FixShipFactionEnabled; } }

        /// <inheritdoc />
        public override void Init(ITorchBase torch) {
            base.Init(torch);

             var configFile = Path.Combine(StoragePath, "ShipFixer.cfg");

            try {

                _config = Persistent<ShipFixerConfig>.Load(configFile);

            } catch (Exception e) {
                Log.Warn(e);
            }

            if (_config?.Data == null) {

                Log.Info("Create Default Config, because none was found!");

                _config = new Persistent<ShipFixerConfig>(configFile, new ShipFixerConfig());
                Save();
            }
        }

        public void Save() {
            try {
                _config.Save();
                Log.Info("Configuration Saved.");
            } catch (IOException) {
                Log.Warn("Configuration failed to save");
            }
        }

        public CheckResult FixShip(IMyCharacter character, long playerId) {

            ConcurrentBag<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group> groups = FindLookAtGridGroup(character, playerId, FactionFixEnabled);

            return FixGroups(groups, playerId);
        }

        public CheckResult FixShip(string gridName, long playerId) {

            ConcurrentBag<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group> groups = FindGridGroupsForPlayer(gridName, playerId, FactionFixEnabled);

            return FixGroups(groups, playerId);
        }

        public static CheckResult CheckGroups(ConcurrentBag<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group> groups, 
            out MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group group, long playerId, bool factionFixEnabled) {

            /* No group or too many groups found */
            if (groups.Count < 1) {
                group = null;
                return CheckResult.TOO_FEW_GRIDS;
            }

            /* too many groups found */
            if (groups.Count > 1) {
                group = null;
                return CheckResult.TOO_MANY_GRIDS;
            } 
            
            if (!groups.TryPeek(out group)) 
                return CheckResult.UNKNOWN_PROBLEM;

            /* Check if there are Connected grids owned by a different player */
            if (playerId != 0) {

                MyCubeGrid referenceGrid = null;

                foreach (MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Node groupNodes in group.Nodes) {

                    MyCubeGrid grid = groupNodes.NodeData;

                    if (grid.Physics == null)
                        continue;

                    /* We are not the server and playerId is not owner */
                    if (!OwnershipCorrect(grid, playerId, factionFixEnabled))
                        continue;

                    referenceGrid = grid;
                    break;
                }

                if (referenceGrid == null) 
                    return CheckResult.OWNED_BY_DIFFERENT_PLAYER;

                foreach (MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Node groupNodes in group.Nodes) {

                    MyCubeGrid grid = groupNodes.NodeData;

                    if (grid.Physics == null)
                        continue;

                    if (grid == referenceGrid)
                        continue;

                    if (grid.IsSameConstructAs(referenceGrid))
                        continue;

                    /* We are not the server and playerId is not owner */
                    if (!OwnershipCorrect(grid, playerId, factionFixEnabled))
                        return CheckResult.OWNED_BY_DIFFERENT_PLAYER;
                }
            }

            /* Check if there are people in the cockpit */
            foreach (MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Node groupNodes in group.Nodes) {

                IMyCubeGrid grid = groupNodes.NodeData;

                List<IMyTerminalBlock> tBlockList = new List<IMyTerminalBlock>();

                var gts = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
                gts.GetBlocksOfType<Sandbox.ModAPI.IMyTerminalBlock>(tBlockList);

                foreach (var block in tBlockList) {
                    if (block == null)
                        continue;

                    if (block is IMyShipController controller && controller.IsUnderControl)
                        return CheckResult.GRID_OCCUPIED;
                }
            }

            return CheckResult.OK;
        }

        private CheckResult FixGroups(ConcurrentBag<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group> groups, long playerId) {

            var result = CheckGroups(groups, out MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group group, playerId, FactionFixEnabled);

            if (result != CheckResult.OK)
                return result;

            MyIdentity executingPlayer = null;

            if (playerId != 0)
                executingPlayer = PlayerUtils.GetIdentityById(playerId);

            return FixGroup(group, executingPlayer);
        }

        private CheckResult FixGroup(MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group group, MyIdentity executingPlayer) {

            string playerName = "Server";

            if (executingPlayer != null)
                playerName = executingPlayer.DisplayName;

            List<MyObjectBuilder_EntityBase> objectBuilderList = new List<MyObjectBuilder_EntityBase>();
            List<MyCubeGrid> gridsList = new List<MyCubeGrid>();

            foreach (MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Node groupNodes in group.Nodes) {

                MyCubeGrid grid = groupNodes.NodeData;
                gridsList.Add(grid);

                grid.Physics.LinearVelocity = Vector3.Zero;

                MyObjectBuilder_EntityBase ob = grid.GetObjectBuilder(true);

                if (!objectBuilderList.Contains(ob)) {

                    if (ob is MyObjectBuilder_CubeGrid gridBuilder) {

                        foreach (MyObjectBuilder_CubeBlock cubeBlock in gridBuilder.CubeBlocks) {

                            if (Config.RemoveBlueprintsFromProjectors)
                                if (cubeBlock is MyObjectBuilder_ProjectorBase projector)
                                    projector.ProjectedGrids = null;

                            if (cubeBlock is MyObjectBuilder_OxygenTank o2Tank)
                                o2Tank.AutoRefill = false;
                        }
                    }

                    objectBuilderList.Add(ob);
                }
            }

            foreach (MyCubeGrid grid in gridsList) {

                var entity = grid as IMyEntity;

                Log.Warn("Player " + playerName + " used ShipFixerPlugin on Grid " + grid.DisplayName + " for cut & paste!");

                entity.Close();
            }

            MyAPIGateway.Entities.RemapObjectBuilderCollection(objectBuilderList);

            bool hasMultipleGrids = objectBuilderList.Count > 1;

            if (!hasMultipleGrids) {

                foreach (var ob in objectBuilderList)
                    MyEntities.CreateFromObjectBuilderParallel(ob, true);

            } else {

                MyEntities.Load(objectBuilderList, out _);
            }

            return CheckResult.SHIP_FIXED;
        }

        public static ConcurrentBag<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group> FindLookAtGridGroup(IMyCharacter controlledEntity, long playerId, bool factionFixEnabled) {

            const float range = 5000;
            Matrix worldMatrix;
            Vector3D startPosition;
            Vector3D endPosition;

            worldMatrix = controlledEntity.GetHeadMatrix(true, true, false); // dead center of player cross hairs, or the direction the player is looking with ALT.
            startPosition = worldMatrix.Translation + worldMatrix.Forward * 0.5f;
            endPosition = worldMatrix.Translation + worldMatrix.Forward * (range + 0.5f);

            var list = new Dictionary<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group, double>();
            var ray = new RayD(startPosition, worldMatrix.Forward);

            foreach(var group in MyCubeGridGroups.Static.Physical.Groups) {

                foreach (MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Node groupNodes in group.Nodes) {

                    MyCubeGrid cubeGrid = groupNodes.NodeData;

                    if (cubeGrid != null) {

                        if (cubeGrid.Physics == null)
                            continue;

                        /* We are not the server and playerId is not owner */
                        if (playerId != 0 && !OwnershipCorrect(cubeGrid, playerId, factionFixEnabled))
                            continue;

                        // check if the ray comes anywhere near the Grid before continuing.    
                        if (ray.Intersects(cubeGrid.PositionComp.WorldAABB).HasValue) {

                            Vector3I? hit = cubeGrid.RayCastBlocks(startPosition, endPosition);

                            if (hit.HasValue) {

                                double distance = (startPosition - cubeGrid.GridIntegerToWorld(hit.Value)).Length();

                                if (list.TryGetValue(group, out double oldDistance)) {

                                    if (distance < oldDistance) {
                                        list.Remove(group);
                                        list.Add(group, distance);
                                    }

                                } else {

                                    list.Add(group, distance);
                                }
                            }
                        }
                    }
                }
            }

            ConcurrentBag<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group> bag = new ConcurrentBag<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group>();

            if (list.Count == 0) 
                return bag;

            // find the closest Entity.
            var item = list.OrderBy(f => f.Value).First();
            bag.Add(item.Key);

            return bag;
        }

        public static ConcurrentBag<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group> FindGridGroupsForPlayer(string gridName, long playerId, bool factionFixEnabled) {

            ConcurrentBag<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group> groups = new ConcurrentBag<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group>();
            Parallel.ForEach(MyCubeGridGroups.Static.Physical.Groups, group => {

                foreach (MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Node groupNodes in group.Nodes) {

                    MyCubeGrid grid = groupNodes.NodeData;

                    if (grid.Physics == null)
                        continue;

                    /* Gridname is wrong ignore */
                    if (!grid.DisplayName.Equals(gridName))
                        continue;

                    /* We are not the server and playerId is not owner */
                    if (playerId != 0 && !OwnershipCorrect(grid, playerId, factionFixEnabled))
                        continue;

                    groups.Add(group);
                    break;
                }
            });

            return groups;
        }

        public static bool OwnershipCorrect(MyCubeGrid grid, long playerId, bool checkFactions) {

            /* If Player is owner we are totally fine and can allow it */
            if(grid.BigOwners.Contains(playerId))
                return true;

            /* If he is not owner and we dont want to allow checks for faction members... then prohibit */
            if (!checkFactions)
                return false;

            /* If checks for faction are allowed grab owner and see if factions are equal */
            long gridOwner = OwnershipUtils.GetOwner(grid);

            return FactionUtils.HavePlayersSameFaction(playerId, gridOwner);
        }
    }
}