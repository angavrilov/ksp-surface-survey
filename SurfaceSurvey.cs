using System;
using UnityEngine;

namespace SurfaceSurvey
{
    public class SurfaceSurveyModule : PartModule
    {
        // Experiment name & transmit ratio (like in stock experiment)
        [KSPField(isPersistant=false)]
        public string experimentID;

        public ScienceExperiment experiment;

        [KSPField(isPersistant=false)]
        public float xmitDataScalar = 1f;

        // Seconds to generate one base amount of science
        [KSPField(isPersistant=false)]
        public float oneReportSeconds = 60f;

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
            if (velocityCurve == null)
                velocityCurve = new FloatCurve();
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
                    dataRate = maxReportData / oneReportSeconds;
                }

                container = part.Modules["ModuleScienceContainer"] as ModuleScienceContainer;
                if (container == null)
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

            if (experiment == null || vessel == null || container == null || !part.isControllable)
            {
                statusString = "Disconnected";
                return;
            }

            if (TimeWarp.CurrentRate > 1f && TimeWarp.WarpMode == TimeWarp.Modes.HIGH)
                return;

            if (requireControlSource && !part.isControlSource)
            {
                statusString = "No Crew";
                return;
            }

            // Check environment conditions
            var body = vessel.mainBody;
            var situation = getScienceSituation();

            if (!experiment.IsAvailableWhile(situation, body))
            {
                statusString = "Bad Environment";
                return;
            }

            // Verify speed and resources
            float coeff = ComputeSpeedCoeff();

            if (coeff <= 0.0f)
            {
                statusString = "Bad Speed";
                return;
            }

            float data = ComputeRate(coeff);

            if (data <= 0.0f)
            {
                statusString = "No Power";
                return;
            }

            // Compute biome and subject
            string biome = "";
            if (experiment.BiomeIsRelevantWhile(situation))
            {
                biome = vessel.landedAt;
                if (biome == "")
                    biome = Toadicus_GetAtt().name;
            }

            ScienceSubject subject = ResearchAndDevelopment.GetExperimentSubject(experiment, situation, body, biome);

            // Store data in the container
            if (StoreScience(container, subject, data))
            {
                statusString = String.Format("{0:F2}/min", coeff * dataRate * 60);
                if (biome != "")
                    statusString += " (" + biome + ")";
            }
            else
                statusString = "Container Full";
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

        protected float ComputeRate(float coeff)
        {
            if (resourceRate > 0)
            {
                float amount = resourceRate * coeff * TimeWarp.fixedDeltaTime;
                coeff *= Mathf.Min(1f, part.RequestResource(resourceName, amount) / amount);
            }

            return coeff * dataRate * TimeWarp.fixedDeltaTime;
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

            var new_data = new ScienceData(data, xmitDataScalar, subject.id, subject.title);

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

        // From here: http://forum.kerbalspaceprogram.com/threads/53709-Adding-Biome-Detection-to-Plugin
        // The code is part of VOID, available under GNU GPL.
        protected CBAttributeMap.MapAttribute Toadicus_GetAtt()
        {
            CBAttributeMap.MapAttribute mapAttribute;

            try
            {
                CBAttributeMap BiomeMap = vessel.mainBody.BiomeMap;
                double lat = vessel.latitude * Math.PI / 180d;
                double lon = vessel.longitude * Math.PI / 180d;

                lon -= Math.PI / 2d;

                if (lon < 0d)
                {
                    lon += 2d * Math.PI;
                }

                float v = (float)(lat / Math.PI) + 0.5f;
                float u = (float)(lon / (2d * Math.PI));

                Color pixelBilinear = BiomeMap.Map.GetPixelBilinear(u, v);
                mapAttribute = BiomeMap.defaultAttribute;

                if (BiomeMap.Map != null)
                {
                    if (BiomeMap.exactSearch)
                    {
                        for (int i = 0; i < BiomeMap.Attributes.Length; ++i)
                        {
                            if (pixelBilinear == BiomeMap.Attributes[i].mapColor)
                            {
                                mapAttribute = BiomeMap.Attributes[i];
                            }
                        }
                    }
                    else
                    {
                        float zero = 0;
                        float num = 1 / zero;
                        for (int j = 0; j < BiomeMap.Attributes.Length; ++j)
                        {
                            Color mapColor = BiomeMap.Attributes[j].mapColor;
                            float sqrMagnitude = ((Vector4)(mapColor - pixelBilinear)).sqrMagnitude;
                            if (sqrMagnitude < num)
                            {
                                bool testCase = true;
                                if (BiomeMap.nonExactThreshold != -1)
                                {
                                    testCase = (sqrMagnitude < BiomeMap.nonExactThreshold);
                                }
                                if (testCase)
                                {
                                    mapAttribute = BiomeMap.Attributes[j];
                                    num = sqrMagnitude;
                                }
                            }
                        }
                    }
                }
            }
            catch (NullReferenceException)
            {
                mapAttribute = new CBAttributeMap.MapAttribute();
                mapAttribute.name = "N/A";
            }

            return mapAttribute;
        }
    }
}

