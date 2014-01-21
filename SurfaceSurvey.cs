using System;
using System.ComponentModel;
using System.Collections.Generic;
using UnityEngine;

namespace SurfaceSurvey
{
    public class SurfaceSurveyModule : PartModule
    {
        // Experiment name & transmit ratio (like in stock experiment)
        [KSPField(isPersistant=false)]
        public string experimentID;

        private ScienceExperiment experiment;

        [KSPField(isPersistant=false)]
        public ConfigDictionary<float> xmitDataScalar;

        [SerializeField]
        private ConfigNode xmitDataScalar_backup;

        // Base science (before biome coeff) to generate per minute
        [KSPField(isPersistant=false)]
        public ConfigDictionary<float> sciencePerMin;

        [SerializeField]
        private ConfigNode sciencePerMin_backup;

        private float maxReportData = 0f;
        private float dataRate = 0f;

        // Resource to consume per second
        [KSPField(isPersistant=false)]
        public string resourceName = "ElectricCharge";

        [KSPField(isPersistant=false)]
        public float resourceRate = 0f;

        // Require that the current part be a control source (i.e. have crew if manned pod)
        [KSPField(isPersistant=false)]
        public bool requireControlSource = false;

        // Min surface velocity required for operation
        [KSPField(isPersistant=false)]
        public float minVelocity = 1f;

        // Scaling factor to tweak data collection rate based on velocity
        [KSPField(isPersistant=false)]
        public FloatCurve velocityCurve;

        // Activity state
        [KSPField(isPersistant=true)]
        public bool isActive = false;

        // Container to use for storing science
        private ModuleScienceContainer container;
        private KerbalSeat seat;
        private bool containerFull = false;

        // Actions and status string
        [KSPField(isPersistant=false)]
        public string surveyName = "Survey";

        [KSPField(isPersistant=false, guiActive = true)]
        public string statusString = "Disabled";

        [KSPAction("Toggle")]
        public void Toggle(KSPActionParam param)
        {
            OnToggle();
        }

        [KSPEvent (guiName = "Toggle", guiActive = true)]
        public void OnToggle()
        {
            isActive = !isActive;
            UpdateStatus();
        }

        // Initialization
        public override void OnAwake()
        {
            ConfigDictionary<float>.AwakeInit(ref xmitDataScalar, ref xmitDataScalar_backup, 1f);
            ConfigDictionary<float>.AwakeInit(ref sciencePerMin, ref sciencePerMin_backup, 1f);

            if (velocityCurve == null)
                velocityCurve = new FloatCurve();
        }

        public override void OnLoad (ConfigNode node)
        {
            base.OnLoad(node);

            xmitDataScalar.LoadDefault(node.GetValue("xmitDataScalar"));
            sciencePerMin.LoadDefault(node.GetValue("sciencePerMin"));
        }

        public override void OnStart(PartModule.StartState state)
        {
            base.OnStart(state);

            if (state != PartModule.StartState.Editor)
            {
                experiment = ResearchAndDevelopment.GetExperiment(experimentID);
                if (experiment != null)
                {
                    maxReportData = experiment.baseValue * experiment.dataScale;
                    dataRate = experiment.dataScale / 60.0f;
                }


                container = part.Modules["ModuleScienceContainer"] as ModuleScienceContainer;
                seat = part.Modules["KerbalSeat"] as KerbalSeat;

                if (container == null && seat == null)
                    Debug.Log("No ModuleScienceContainer found in SurfaceSurveyModule.OnStart");
            }

            UpdateActions();
        }

        private void UpdateActions()
        {
            Fields["statusString"].guiName = surveyName;
            Events["OnToggle"].guiName = Actions["Toggle"].guiName = "Toggle " + surveyName;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            containerFull = false;
            if (!isActive)
                statusString = "Disabled";
            else
                statusString = "Activating";
        }

        public void FixedUpdate()
        {
            // Verify basic constraints
            if (!isActive)
                return;

            ModuleScienceContainer container = this.container;

            if (experiment == null || vessel == null || (container == null && seat == null) || !part.isControllable)
            {
                statusString = "Disconnected";
                return;
            }

            if (TimeWarp.CurrentRate > 1f && TimeWarp.WarpMode == TimeWarp.Modes.HIGH)
                return;

            if (requireControlSource && !part.isControlSource && (!seat || !seat.Occupant))
            {
                statusString = "No Crew";
                return;
            }

            if (container == null)
            {
                if (seat && seat.Occupant)
                    container = seat.Occupant.Modules["ModuleScienceContainer"] as ModuleScienceContainer;

                if (container == null)
                {
                    statusString = "No Storage";
                    return;
                }
            }

            // Check environment conditions
            var body = vessel.mainBody;
            var situation = getScienceSituation();

            if (!experiment.IsAvailableWhile(situation, body))
            {
                statusString = "Wrong Environment";
                return;
            }

            // Verify speed and resources
            float coeff = ComputeSpeedCoeff();

            if (coeff <= 0.0f)
            {
                statusString = "Wrong Speed";
                return;
            }

            ConsumeResource(ref coeff);

            if (coeff <= 0.0f)
            {
                statusString = "No Power";
                return;
            }

            float dataFlow = dataRate * coeff * sciencePerMin[body.name];

            // Compute biome and subject
            string biome = "";
            if (experiment.BiomeIsRelevantWhile(situation))
            {
                biome = vessel.landedAt;
                if (biome == "")
                {
                    CBAttributeMap BiomeMap = vessel.mainBody.BiomeMap;
                    double lat = vessel.latitude * Math.PI / 180d;
                    double lon = vessel.longitude * Math.PI / 180d;
                    biome = BiomeMap.GetAtt(lat, lon).name;
                }
            }

            ScienceSubject subject = ResearchAndDevelopment.GetExperimentSubject(experiment, situation, body, biome);

            // Store data in the container
            if (StoreScience(container, subject, dataFlow * TimeWarp.fixedDeltaTime))
            {
                statusString = String.Format("{0:F2}/min", dataFlow * 60);
                if (biome != "")
                    statusString += " (" + biome + ")";

                containerFull = false;
            }
            else
            {
                statusString = "Container Full";

                if (!containerFull)
                {
                    ScreenMessages.PostScreenMessage(
                        "<color=#ff9900ff>["+part.partInfo.title+"] <i>"+subject.title+":</i> Container Full.</color>",
                        10f, ScreenMessageStyle.UPPER_LEFT
                    );

                    containerFull = true;
                }
            }
        }

        protected float ComputeSpeedCoeff()
        {
            float speed = (float)Vector3d.Exclude(vessel.upAxis, vessel.GetSrfVelocity()).magnitude;
            if (speed < minVelocity)
                return 0.0f;

            float coeff = 1.0f;
            if (velocityCurve.minTime < velocityCurve.maxTime)
                coeff = velocityCurve.Evaluate(speed);

            return coeff;
        }

        protected void ConsumeResource(ref float coeff)
        {
            if (resourceRate > 0)
            {
                float amount = resourceRate * coeff * TimeWarp.fixedDeltaTime;
                coeff *= Mathf.Min(1f, part.RequestResource(resourceName, amount) / amount);
            }
        }

        protected bool StoreScience(ModuleScienceContainer container, ScienceSubject subject, float data)
        {
            // First try to increment data in an existing report
            foreach (var item in container.GetData())
            {
                if (item == null || item.subjectID != subject.id)
                    continue;

                // Limit amount of data per report object
                float sum = item.dataAmount + data;
                item.dataAmount = Mathf.Min(maxReportData, sum);
                data = sum - item.dataAmount;

                if (data <= 0f)
                    return true;

                if (!container.allowRepeatedSubjects)
                    return false;
            }

            // Check constraints before calling to avoid status spam from the container itself
            if (container.capacity > 0 && container.GetScienceCount() >= container.capacity)
                return false;

            float xmitValue = xmitDataScalar[vessel.mainBody.name];
            var new_data = new ScienceData(data, xmitValue, 0f, subject.id, subject.title);

            if (container.AddData(new_data))
                return true;

            isActive = false;
            return false;
        }

        // Based on a function from Station Science, available under GNU GPL.
        // http://forum.kerbalspaceprogram.com/threads/54774-0-22-Station-Science-%28third-alpha-Zoology-Bay-added%29
        protected ExperimentSituations getScienceSituation()
        {
            switch (vessel.situation)
            {
            case Vessel.Situations.LANDED:
            case Vessel.Situations.PRELAUNCH:
                return ExperimentSituations.SrfLanded;
            case Vessel.Situations.SPLASHED:
                return ExperimentSituations.SrfSplashed;
            case Vessel.Situations.FLYING:
                if (vessel.altitude < vessel.mainBody.scienceValues.flyingAltitudeThreshold)
                    return ExperimentSituations.FlyingLow;
                else
                    return ExperimentSituations.FlyingHigh;
            default:
                if (vessel.altitude < vessel.mainBody.scienceValues.spaceAltitudeThreshold)
                    return ExperimentSituations.InSpaceLow;
                else
                    return ExperimentSituations.InSpaceHigh;
            }
        }
    }
}

