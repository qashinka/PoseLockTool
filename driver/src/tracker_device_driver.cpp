//============ Copyright (c) Valve Corporation, All rights reserved. ============
#include "tracker_device_driver.h"

#include "driverlog.h"
#include "vrmath.h"



// Let's create some variables for strings used in getting settings.
// This is the section where all of the settings we want are stored. A section name can be anything,
// but if you want to store driver specific settings, it's best to namespace the section with the driver identifier
// ie "<my_driver>_<section>" to avoid collisions
static const char *my_tracker_main_settings_section = "driver_simpletrackers";

// These are the keys we want to retrieve the values for in the settings
static const char *my_tracker_settings_key_model_number = "mytracker_model_number";

MyTrackerDeviceDriver::MyTrackerDeviceDriver( unsigned int my_tracker_id )
{
	// Set a member to keep track of whether we've activated yet or not
	is_active_ = false;
	has_last_known_good_pose_ = false;
	pose_locking_enabled_ = false;
	proxy_mode_enabled_ = false;
	target_device_index_ = vr::k_unTrackedDeviceIndexInvalid; // k_unTrackedDeviceIndexInvalid means no device

	my_tracker_id_ = my_tracker_id;

	// We have our model number and serial number stored in SteamVR settings. We need to get them and do so here.
	// Other IVRSettings methods (to get int32, floats, bools) return the data, instead of modifying, but strings are
	// different.
	my_device_model_number_ = "MyTrackerModelNumber";

	// of our driver.
	// Emulate a serial number by appending the internal tracker id we are given by our implementation of
	// IServerTrackedDeviceProvider
	my_device_serial_number_ = my_device_model_number_ + std::to_string( my_tracker_id );

	// Here's an example of how to use our logging wrapper around IVRDriverLog
	// In SteamVR logs (SteamVR Hamburger Menu > Developer Settings > Web console) drivers have a prefix of
	// "<driver_name>:". You can search this in the top search bar to find the info that you've logged.
	DriverLog( "My Controller Model Number: %s", my_device_model_number_.c_str() );
	DriverLog( "My Controller Serial Number: %s", my_device_serial_number_.c_str() );
}

//-----------------------------------------------------------------------------
// Purpose: This is called by vrserver after our
//  IServerTrackedDeviceProvider calls IVRServerDriverHost::TrackedDeviceAdded.
//-----------------------------------------------------------------------------
vr::EVRInitError MyTrackerDeviceDriver::Activate( uint32_t unObjectId )
{
	// Set an member to keep track of whether we've activated yet or not
	is_active_ = true;

	// Let's keep track of our device index. It'll be useful later.
	my_device_index_ = unObjectId;

	// Properties are stored in containers, usually one container per device index. We need to get this container to set
	// The properties we want, so we call this to retrieve a handle to it.
	vr::PropertyContainerHandle_t container = vr::VRProperties()->TrackedDeviceToPropertyContainer( my_device_index_ );

	// Let's begin setting up the properties now we've got our container.
	// A list of properties available is contained in vr::ETrackedDeviceProperty.

	// First, let's set the model number.
	vr::VRProperties()->SetStringProperty( container, vr::Prop_ModelNumber_String, my_device_model_number_.c_str() );

	// --- Our new code to read settings ---
	// Let's get the settings to see if we should enable pose locking for this device
	const char* settings_section = "PoseLockDriver";
	const char* settings_key = "enabled_trackers";
	char enabled_trackers_buffer[1024];
	vr::VRSettings()->GetString(settings_section, settings_key, enabled_trackers_buffer, sizeof(enabled_trackers_buffer));

	// Check if our serial number is in the list.
	// This is a simple substring search. A more robust solution would be to parse the comma-separated list.
	if (std::string(enabled_trackers_buffer).find(my_device_serial_number_) != std::string::npos)
	{
		pose_locking_enabled_ = true;
		DriverLog("Pose locking ENABLED for tracker %s", my_device_serial_number_.c_str());
	}
	else
	{
		pose_locking_enabled_ = false;
		DriverLog("Pose locking DISABLED for tracker %s", my_device_serial_number_.c_str());
	}
	// --- End of our new code ---


	// Now let's set up our inputs

	// This tells the UI what to show the user for bindings for this controller,
	// As well as what default bindings should be for legacy apps.
	// Note, we can use the wildcard {<driver_name>} to match the root folder location
	// of our driver.
	vr::VRProperties()->SetStringProperty(
		container, vr::Prop_InputProfilePath_String, "{simpletrackers}/input/mytracker_profile.json" );

	// Let's set up handles for all of our components.
	// Even though these are also defined in our input profile,
	// We need to get handles to them to update the inputs.

	// Let's set up our "A" button. We've defined it to have a touch and a click component.
	vr::VRDriverInput()->CreateBooleanComponent( container, "/input/a/touch", &input_handles_[ MyComponent_a_touch ] );
	vr::VRDriverInput()->CreateBooleanComponent( container, "/input/a/click", &input_handles_[ MyComponent_a_click ] );

	// Let's set up our trigger. We've defined it to have a value and click component.

	// CreateScalarComponent requires:
	// EVRScalarType - whether the device can give an absolute position, or just one relative to where it was last. We
	// can do it absolute.
	// EVRScalarUnits - whether the devices has two "sides", like a joystick. This makes the range of valid inputs -1
	// to 1. Otherwise, it's 0 to 1. We only have one "side", so ours is onesided.
	vr::VRDriverInput()->CreateScalarComponent( container, "/input/trigger/value",
		&input_handles_[ MyComponent_trigger_value ], vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedOneSided );
	vr::VRDriverInput()->CreateBooleanComponent(
		container, "/input/trigger/click", &input_handles_[ MyComponent_trigger_click ] );

	my_pose_update_thread_ = std::thread( &MyTrackerDeviceDriver::MyPoseUpdateThread, this );

	// We've activated everything successfully!
	// Let's tell SteamVR that by saying we don't have any errors.
	return vr::VRInitError_None;
}

//-----------------------------------------------------------------------------
// Purpose: If you're an HMD, this is where you would return an implementation
// of vr::IVRDisplayComponent, vr::IVRVirtualDisplay or vr::IVRDirectModeComponent.
//
// But this a simple example to demo for a controller, so we'll just return nullptr here.
//-----------------------------------------------------------------------------
void *MyTrackerDeviceDriver::GetComponent( const char *pchComponentNameAndVersion )
{
	return nullptr;
}

//-----------------------------------------------------------------------------
// Purpose: This is called by vrserver when a debug request has been made from an application to the driver.
// What is in the response and request is up to the application and driver to figure out themselves.
//-----------------------------------------------------------------------------
void MyTrackerDeviceDriver::DebugRequest(
	const char *pchRequest, char *pchResponseBuffer, uint32_t unResponseBufferSize )
{
	if ( unResponseBufferSize >= 1 )
		pchResponseBuffer[ 0 ] = 0;
}

//-----------------------------------------------------------------------------
// Purpose: This is never called by vrserver in recent OpenVR versions,
// but is useful for giving data to vr::VRServerDriverHost::TrackedDevicePoseUpdated.
//-----------------------------------------------------------------------------
vr::DriverPose_t MyTrackerDeviceDriver::GetPose()
{
	// First, initialize the struct that we'll be submitting to the runtime to tell it we've updated our pose.
	vr::DriverPose_t pose = { 0 };

	// These need to be set to be valid quaternions. The device won't appear otherwise.
	pose.qWorldFromDriverRotation.w = 1.f;
	pose.qDriverFromHeadRotation.w = 1.f;

	pose.deviceIsConnected = true; // Assume the device is always connected

	if (proxy_mode_enabled_ && target_device_index_ != vr::k_unTrackedDeviceIndexInvalid)
	{
		// --- PROXY MODE --- 
		// Get the poses of all tracked devices
		vr::TrackedDevicePose_t all_poses[vr::k_unMaxTrackedDeviceCount];
		vr::VRServerDriverHost()->GetRawTrackedDevicePoses(0, all_poses, vr::k_unMaxTrackedDeviceCount);

		// Get the pose of our target device
		const vr::TrackedDevicePose_t& target_pose = all_poses[target_device_index_];

		// Copy the target's state
		pose.poseIsValid = target_pose.bPoseIsValid;
		pose.result = target_pose.eTrackingResult;

		// Copy the target's position and orientation
		pose.vecPosition[0] = target_pose.mDeviceToAbsoluteTracking.m[0][3];
		pose.vecPosition[1] = target_pose.mDeviceToAbsoluteTracking.m[1][3];
		pose.vecPosition[2] = target_pose.mDeviceToAbsoluteTracking.m[2][3];

		pose.qRotation = HmdQuaternion_FromMatrix(target_pose.mDeviceToAbsoluteTracking);
	}
	else
	{
		// --- DEFAULT (HMD-TRACKING) MODE ---
		// Get the HMD pose
		vr::TrackedDevicePose_t hmd_pose;
		vr::VRServerDriverHost()->GetRawTrackedDevicePoses(0.f, &hmd_pose, 1);

		if (hmd_pose.bPoseIsValid)
		{
			// Get the position and orientation of the HMD
			const vr::HmdVector3_t hmd_position = HmdVector3_From34Matrix(hmd_pose.mDeviceToAbsoluteTracking);
			const vr::HmdQuaternion_t hmd_orientation = HmdQuaternion_FromMatrix(hmd_pose.mDeviceToAbsoluteTracking);

			// Set the pose orientation to the HMD orientation
			pose.qRotation = hmd_orientation;

			const vr::HmdVector3_t offset_position = {
				-0.15f + my_tracker_id_ * 0.15f, // translate our tracker depending on the id we were provided
				0.1f,                            // shift it up a little to make it more in view
				-0.5f,                           // put each controller 0.5m forward in front of the hmd so we can see it.
			};

			// Rotate our offset by the HMD quaternion and add the HMD's position
			const vr::HmdVector3_t final_position = hmd_position + (offset_position * hmd_orientation);

			// Copy our position to our pose
			pose.vecPosition[0] = final_position.v[0];
			pose.vecPosition[1] = final_position.v[1];
			pose.vecPosition[2] = final_position.v[2];

			pose.poseIsValid = true;
			pose.result = vr::TrackingResult_Running_OK;
		}
		else
		{
			// HMD pose is not valid, so we can't do anything.
			pose.poseIsValid = false;
			pose.result = vr::TrackingResult_Uninitialized;
		}
	}

	return pose;
}

void MyTrackerDeviceDriver::MyPoseUpdateThread()
{
	while ( is_active_ )
	{
		// --- Read proxy settings --- 
		const char* settings_section = "PoseLockProxy";
		// Construct the key for this specific tracker, e.g., "proxy_target_for_MyTrackerModelNumber10"
		std::string key = "proxy_target_for_" + my_device_serial_number_;

		// Read the target device index from settings. Default to -1 (invalid) if not found.
		vr::EVRSettingsError eError = vr::VRSettingsError_None;
		int32_t target_index = vr::VRSettings()->GetInt32(settings_section, key.c_str(), &eError);

		if (eError != vr::VRSettingsError_None)
		{
			target_index = -1;
		}

		if (target_index != -1)
		{
			// A valid target is set, so enable proxy mode
			proxy_mode_enabled_ = true;
			target_device_index_ = (uint32_t)target_index;
		}
		else
		{
			// No target is set for this tracker, so disable proxy mode
			proxy_mode_enabled_ = false;
			target_device_index_ = vr::k_unTrackedDeviceIndexInvalid;
		}

		// --- Original Pose Locking Logic ---
		if (pose_locking_enabled_)
		{
			// --- POSE LOCKING LOGIC (for real hardware) ---
			
			// Get the pose from the device. GetPose() would now read from your actual hardware.
			// We assume it sets pose.poseIsValid correctly based on the hardware's tracking state.
			vr::DriverPose_t current_pose = GetPose();

			// Check if the pose is valid
			if ( current_pose.poseIsValid )
			{
				// It's valid, so we should update our last known good pose
				last_known_good_pose_ = current_pose;
				has_last_known_good_pose_ = true;
			}

			// If we have a last known good pose, send it to SteamVR
			if ( has_last_known_good_pose_ )
			{
				// We need to make sure to mark it as valid before sending
				last_known_good_pose_.poseIsValid = true;
				vr::VRServerDriverHost()->TrackedDevicePoseUpdated( my_device_index_, last_known_good_pose_, sizeof( vr::DriverPose_t ) );
			}
		}
		else
		{
			// --- DEFAULT LOGIC ---
			// Pose locking is disabled, so just send the latest pose directly.
			vr::VRServerDriverHost()->TrackedDevicePoseUpdated( my_device_index_, GetPose(), sizeof( vr::DriverPose_t ) );
		}


		// Update our pose every five milliseconds.
		// In reality, you should update the pose whenever you have new data from your device.
		std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
	}
}

//-----------------------------------------------------------------------------
// Purpose: This is called by vrserver when the device should enter standby mode.
// The device should be put into whatever low power mode it has.
// We don't really have anything to do here, so let's just log something.
//-----------------------------------------------------------------------------
void MyTrackerDeviceDriver::EnterStandby()
{
	DriverLog( "Tracker has been put into standby" );
}

//-----------------------------------------------------------------------------
// Purpose: This is called by vrserver when the device should deactivate.
// This is typically at the end of a session
// The device should free any resources it has allocated here.
//-----------------------------------------------------------------------------
void MyTrackerDeviceDriver::Deactivate()
{
	// Let's join our pose thread that's running
	// by first checking then setting is_active_ to false to break out
	// of the while loop, if it's running, then call .join() on the thread
	if ( is_active_.exchange( false ) )
	{
		my_pose_update_thread_.join();
	}

	// unassign our controller index (we don't want to be calling vrserver anymore after Deactivate() has been called
	my_device_index_ = vr::k_unTrackedDeviceIndexInvalid;
}


//-----------------------------------------------------------------------------
// Purpose: This is called by our IServerTrackedDeviceProvider when its RunFrame() method gets called.
// It's not part of the ITrackedDeviceServerDriver interface, we created it ourselves.
//-----------------------------------------------------------------------------
void MyTrackerDeviceDriver::MyRunFrame()
{
	// update our inputs here
	vr::VRDriverInput()->UpdateBooleanComponent( input_handles_[ MyComponent_a_click ], false, 0 );
	vr::VRDriverInput()->UpdateBooleanComponent( input_handles_[ MyComponent_a_touch ], false, 0 );

	vr::VRDriverInput()->UpdateBooleanComponent( input_handles_[ MyComponent_trigger_click ], false, 0 );
	vr::VRDriverInput()->UpdateScalarComponent( input_handles_[ MyComponent_trigger_value ], 0.f, 0 );
}


//-----------------------------------------------------------------------------
// Purpose: This is called by our IServerTrackedDeviceProvider when it pops an event off the event queue.
// It's not part of the ITrackedDeviceServerDriver interface, we created it ourselves.
//-----------------------------------------------------------------------------
void MyTrackerDeviceDriver::MyProcessEvent( const vr::VREvent_t &vrevent )
{
	// Our tracker doesn't have any events it wants to process.
}

//-----------------------------------------------------------------------------
// Purpose: Our IServerTrackedDeviceProvider needs our serial number to add us to vrserver.
// It's not part of the ITrackedDeviceServerDriver interface, we created it ourselves.
//-----------------------------------------------------------------------------
const std::string &MyTrackerDeviceDriver::MyGetSerialNumber()
{
	return my_device_serial_number_;
}